using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Core;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// Props class for module store entries.
/// Fields mirror <see cref="TsakModuleRecord"/> for typed query access.
/// </summary>
public class TsakModuleProps
{
    public string ModuleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = [];
    public TsakModuleStatus Status { get; set; } = TsakModuleStatus.Discovered;
    public string? AssemblyPath { get; set; }
}

/// <summary>
/// Redb-backed module store. Persists module metadata across restarts and cluster nodes.
/// The module name lives in <c>ValueUnique</c>: one record per module is enforced by the
/// database, and <c>SaveByUniqueAsync</c> makes <see cref="SaveAsync"/> a true upsert —
/// two nodes discovering the same module concurrently converge on one record.
/// <c>value_string</c> still mirrors the name for dashboard visibility.
/// Every operation awaits a one-time ensure step (scheme sync + legacy backfill), so hosts
/// without TsakHostedService and reads racing its startup see pre-V4 rows
/// (review 2026-09-02, В4).
/// </summary>
public class RedbTsakModuleStore : ITsakModuleStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private volatile bool _ensured;

    public RedbTsakModuleStore(IServiceScopeFactory scopeFactory, ILogger<RedbTsakModuleStore>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<RedbTsakModuleStore>.Instance;
    }

    public async Task<IReadOnlyList<TsakModuleRecord>> GetAllAsync()
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var objects = await redb.Query<TsakModuleProps>().ToListAsync();
        return objects.Select(ToRecord).ToList();
    }

    public async Task<TsakModuleRecord?> GetAsync(string moduleName)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueUnique == moduleName)
            .FirstOrDefaultAsync();

        return obj is not null ? ToRecord(obj) : null;
    }

    public async Task SaveAsync(TsakModuleRecord record)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        // The Remove+Save+Save interleave (review С2) is covered by the core since BR-8 closed:
        // SaveByUniqueAsync retries an ObjectKey race onto the current winner itself.
        await redb.SaveByUniqueAsync(new RedbObject<TsakModuleProps>
        {
            name = record.ModuleName,
            ValueUnique = record.ModuleName,
            value_string = record.ModuleName,
            value_long = (long)record.Status,
            note = record.Description,
            Props = ToProps(record)
        });
    }

    public async Task RemoveAsync(string moduleName)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueUnique == moduleName)
            .FirstOrDefaultAsync();

        if (existing is not null)
            await redb.DeleteAsync(existing);
    }

    public async Task SaveBatchAsync(IEnumerable<TsakModuleRecord> records)
    {
        foreach (var record in records)
            await SaveAsync(record);
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
            await redb.SyncSchemeAsync<TsakModuleProps>();
            await BackfillLegacyAsync(redb, _logger);
            _ensured = true; // only after success — a failure retries on the next call
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// The ONE home of the module-store legacy migration rule. Called from the store's ensure
    /// step and from TsakHostedService startup.
    /// </summary>
    internal static Task BackfillLegacyAsync(IRedbService redb, ILogger logger) =>
        TsakUniqueBackfill.RunAsync<TsakModuleProps>(
            redb,
            o => o.ValueString ?? o.Props?.ModuleName,
            TsakUniqueBackfill.DuplicatePolicy.DeleteLosers,
            logger);

    // ── Mapping helpers ──────────────────────────────────────────────

    private static TsakModuleRecord ToRecord(RedbObject<TsakModuleProps> obj)
    {
        var props = obj.Props!;
        return new TsakModuleRecord
        {
            ModuleName = props.ModuleName,
            Version = props.Version,
            Description = props.Description,
            Dependencies = props.Dependencies,
            Status = props.Status,
            AssemblyPath = props.AssemblyPath,
            RegisteredAt = obj.date_create,
            LastUpdatedAt = obj.date_modify
        };
    }

    private static TsakModuleProps ToProps(TsakModuleRecord record) => new()
    {
        ModuleName = record.ModuleName,
        Version = record.Version,
        Description = record.Description,
        Dependencies = record.Dependencies.ToList(),
        Status = record.Status,
        AssemblyPath = record.AssemblyPath
    };
}
