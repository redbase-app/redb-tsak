using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Route.RedbCore;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// Props class for state store entries. Scheme is auto-created from type name.
/// </summary>
public class TsakStateProps
{
    /// <summary>State value (stored in Props for typed access; also mirrored to note).</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Redb-backed state store. Persists key-value state across restarts.
/// The key lives in <c>ValueUnique</c>: uniqueness per key is enforced by the database and
/// <c>SaveByUniqueAsync</c> makes <see cref="SetAsync"/> a true upsert — two nodes setting
/// the same key concurrently converge on one record instead of duplicating it.
/// Keys are normalized with <see cref="RedbUniqueKey.Normalize"/> (440-cap of the key column;
/// caller keys embed context/route ids and carry no length contract — review 2026-09-02, В8).
/// <c>value_string</c> still mirrors the raw key (truncated to its own cap) for visibility.
/// Prefix search is server-side and case-sensitive ordinal (owner decision 2026-09-02); the
/// contract note lives on <see cref="ITsakStateStore"/>.
/// Every operation awaits a one-time ensure step (scheme sync + legacy-key backfill), so a
/// host without TsakHostedService — or a read racing its startup — still sees pre-V4 rows
/// (review 2026-09-02, В4).
/// </summary>
public class RedbTsakStateStore : ITsakStateStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private volatile bool _ensured;

    public RedbTsakStateStore(IServiceScopeFactory scopeFactory, ILogger<RedbTsakStateStore>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<RedbTsakStateStore>.Instance;
    }

    private static string KeyOf(string key) => RedbUniqueKey.Normalize(key);

    public async Task<string?> GetAsync(string key)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var stored = KeyOf(key);
        var obj = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueUnique == stored)
            .FirstOrDefaultAsync();

        return obj?.Props?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var stored = KeyOf(key);
        // The Remove+Set+Set interleave (review С2) is covered by the core since BR-8 closed:
        // SaveByUniqueAsync retries an ObjectKey race — including "the resolved row was deleted
        // and the key re-created" — onto the current winner itself.
        await redb.SaveByUniqueAsync(new RedbObject<TsakStateProps>
        {
            name = stored,
            ValueUnique = stored,
            value_string = key.Length <= 440 ? key : key[..440],
            note = value,
            Props = new TsakStateProps { Value = value }
        });
    }

    public async Task RemoveAsync(string key)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var stored = KeyOf(key);
        var existing = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueUnique == stored)
            .FirstOrDefaultAsync();

        if (existing is not null)
            await redb.DeleteAsync(existing);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        // Indexed PREFILTER, not the final answer: LIKE is case-insensitive on SQLite (and a
        // CI-collation MSSQL), so it may return a superset there — the ordinal re-check below
        // makes the case-sensitive contract hold on every provider (BR-6 contract). Metacharacters
        // in the prefix are safe since BR-7 closed: the core treats StartsWith operands as
        // literals on all providers, so the old full-load fallback is gone.
        var matches = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueUnique!.StartsWith(prefix))
            .ToListAsync();

        return matches
            .Where(o => o.ValueUnique is not null
                        && o.ValueUnique.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                o => o.ValueUnique!,
                o => o.Props?.Value ?? string.Empty);
    }

    /// <summary>One-time scheme sync + legacy backfill; safe under concurrency, retried on failure.</summary>
    private async Task EnsureReadyAsync()
    {
        if (_ensured) return;
        await _ensureLock.WaitAsync();
        try
        {
            if (_ensured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<TsakStateProps>();
            await BackfillLegacyAsync(redb, _logger);
            _ensured = true; // only after success — a failure retries on the next call
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// The ONE home of the state-store legacy migration rule (pre-key rows keyed on
    /// value_string). Called from the store's ensure step and from TsakHostedService startup.
    /// </summary>
    internal static Task BackfillLegacyAsync(IRedbService redb, ILogger logger) =>
        TsakUniqueBackfill.RunAsync<TsakStateProps>(
            redb,
            o => o.ValueString is { Length: > 0 } raw ? RedbUniqueKey.Normalize(raw) : null,
            TsakUniqueBackfill.DuplicatePolicy.DeleteLosers,
            logger);
}
