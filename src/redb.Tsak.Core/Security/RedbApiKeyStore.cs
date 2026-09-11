using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Attributes;
using redb.Core.Exceptions;
using redb.Core.Models.Entities;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Props class for API key entries stored as redb objects.
/// </summary>
public class ApiKeyProps
{
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Public identifier for revocation/listing. Unique per scheme via <c>[RedbUnique]</c>
    /// (a duplicate id in config surfaces loudly at seeding); nullable so legacy rows
    /// without an id stay outside the unique index. Mirrored to <c>note</c> for display.
    /// </summary>
    [RedbUnique]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Roles { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>
/// Redb-backed API key store. Persists keys across restarts and cluster nodes.
/// The hash lives in <c>ValueUnique</c> — one record per key is enforced by the database
/// (concurrent seeding on several nodes cannot duplicate), and the lookup on every request
/// is a single probe of the unique index. <c>Props.Id</c> carries its own
/// <c>[RedbUnique]</c> key, so revocation resolves the record by one indexed probe too;
/// rotating a key's secret in config (same id, new hash) updates the id's record in place.
/// <c>value_string</c> still mirrors the hash for compatibility.
/// </summary>
public sealed class RedbApiKeyStore : IApiKeyStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RedbApiKeyStore> _logger;
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private volatile bool _seeded;

    public RedbApiKeyStore(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<RedbApiKeyStore> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// One-time per process: scheme sync, V4 legacy-key backfill, then seeding of pre-hashed
    /// keys from Tsak:Auth:Keys. Serialized and latched only AFTER success — the old code set
    /// the flag first, so a concurrent first request skipped ahead to key lookups that could
    /// not see un-backfilled legacy rows, and one mid-seed exception disabled legacy keys
    /// until restart (review 2026-09-02, В3).
    /// </summary>
    private async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        if (_seeded) return;
        await _seedLock.WaitAsync(ct);
        try
        {
            if (_seeded) return;

            using (var scope = _scopeFactory.CreateScope())
            {
                var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
                await redb.SyncSchemeAsync<ApiKeyProps>();
                await BackfillLegacyKeysAsync(redb, _logger);
            }

            var section = _configuration.GetSection("Tsak:Auth:Keys");
            if (section.Exists())
            {
                foreach (var child in section.GetChildren())
                {
                    var id = child["Id"];
                    var keyHash = child["KeyHash"];
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(keyHash)) continue;

                    var existing = await GetByHashInternalAsync(keyHash, ct);
                    if (existing is not null) continue;

                    var record = new ApiKeyRecord
                    {
                        Id = id,
                        KeyHash = keyHash,
                        Name = child["Name"] ?? "",
                        UserId = child["UserId"],
                        Roles = child["Roles"] ?? "",
                        Revoked = bool.TryParse(child["Revoked"], out var r) && r,
                        ExpiresAt = DateTimeOffset.TryParse(child["ExpiresAt"], out var exp) ? exp : null,
                        CreatedAt = DateTimeOffset.TryParse(child["CreatedAt"], out var cr) ? cr : DateTimeOffset.UtcNow
                    };
                    await SaveCoreAsync(record, ct);
                    _logger.LogInformation("Seeded API key '{Name}' (id={Id}) from config", record.Name, record.Id);
                }
            }

            _seeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    /// <summary>
    /// The ONE home of the api-key legacy migration rule: stamp the hash into ValueUnique and
    /// the id from <c>note</c> into <c>Props.Id</c>. Called from the store's own ensure step
    /// (hosts without TsakHostedService) and from TsakHostedService startup.
    /// </summary>
    internal static Task BackfillLegacyKeysAsync(IRedbService redb, ILogger logger) =>
        Services.Storage.TsakUniqueBackfill.RunAsync<ApiKeyProps>(
            redb, o => o.ValueString ?? o.Props?.KeyHash,
            Services.Storage.TsakUniqueBackfill.DuplicatePolicy.DeleteLosers, logger,
            winner =>
            {
                if (winner.Props is not null && string.IsNullOrEmpty(winner.Props.Id))
                    winner.Props.Id = winner.note;
            });

    public async Task<ApiKeyRecord?> GetByHashAsync(string keyHash, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        return await GetByHashInternalAsync(keyHash, ct);
    }

    private async Task<ApiKeyRecord?> GetByHashInternalAsync(string keyHash, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await redb.Query<ApiKeyProps>()
            .WhereRedb(o => o.ValueUnique == keyHash)
            .FirstOrDefaultAsync();

        return obj is not null ? ToRecord(obj) : null;
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var objects = await redb.Query<ApiKeyProps>().ToListAsync();
        return objects.Select(ToRecord).ToList();
    }

    public async Task SaveAsync(ApiKeyRecord record, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        await SaveCoreAsync(record, ct);
    }

    private async Task SaveCoreAsync(ApiKeyRecord record, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        RedbObject<ApiKeyProps> Compose() => new()
        {
            name = record.Name,
            ValueUnique = record.KeyHash,
            value_string = record.KeyHash,
            value_long = record.Revoked ? 1 : 0,
            note = record.Id,
            Props = ToProps(record)
        };

        try
        {
            await redb.SaveByUniqueAsync(Compose());
        }
        catch (RedbUniqueViolationException uve) when (uve.Kind == RedbUniqueViolationKind.Property)
        {
            // First-class discrimination since BR-8 closed: an ObjectKey race is retried by the
            // core itself, so what reaches us classified as Property is the [RedbUnique] Id
            // index — the id already exists under a DIFFERENT hash, i.e. key rotation (same id,
            // new secret in config). Update the id's record in place — the row swaps its hash
            // key; pre-fix this threw into the auth path and the rotated key never worked
            // (review 2026-09-02, К5).
            var existing = string.IsNullOrEmpty(record.Id)
                ? null
                : await redb.GetByUniqueAsync<ApiKeyProps>(p => p.Id, record.Id);
            if (existing is null)
                throw;

            _logger.LogInformation("API key id '{Id}' rotated: replacing its stored hash", record.Id);
            var updated = Compose();
            updated.id = existing.id;
            await redb.SaveAsync(updated);
        }
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        // Revocation of a legacy key depends on the backfill having stamped Props.Id — this was
        // the one method that skipped the ensure step (review 2026-09-02, С8).
        await EnsureSeededAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await redb.GetByUniqueAsync<ApiKeyProps>(p => p.Id, id);
        if (obj is null) return false;

        obj.Props!.Revoked = true;
        obj.value_long = 1;
        await redb.SaveAsync(obj);
        return true;
    }

    // ── Mapping helpers ──────────────────────────────────────────────

    private static ApiKeyRecord ToRecord(RedbObject<ApiKeyProps> obj)
    {
        var props = obj.Props!;
        return new ApiKeyRecord
        {
            Id = props.Id ?? obj.note ?? "",
            KeyHash = props.KeyHash,
            Name = props.Name,
            UserId = props.UserId,
            Roles = props.Roles,
            CreatedAt = props.CreatedAt,
            ExpiresAt = props.ExpiresAt,
            Revoked = props.Revoked
        };
    }

    private static ApiKeyProps ToProps(ApiKeyRecord record) => new()
    {
        KeyHash = record.KeyHash,
        Id = string.IsNullOrEmpty(record.Id) ? null : record.Id,
        Name = record.Name,
        UserId = record.UserId,
        Roles = record.Roles,
        CreatedAt = record.CreatedAt,
        ExpiresAt = record.ExpiresAt,
        Revoked = record.Revoked
    };
}
