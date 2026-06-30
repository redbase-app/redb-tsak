using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Models.Entities;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Props class for API key entries stored as EAV objects.
/// </summary>
public class ApiKeyProps
{
    public string KeyHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Roles { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>
/// Redb EAV-backed API key store. Persists keys across restarts and cluster nodes.
/// Each key is a <see cref="RedbObject{ApiKeyProps}"/> with <c>value_string</c> = KeyHash (for fast lookup).
/// </summary>
public sealed class RedbApiKeyStore : IApiKeyStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RedbApiKeyStore> _logger;
    private bool _seeded;

    public RedbApiKeyStore(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<RedbApiKeyStore> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Seeds pre-hashed keys from Tsak:Auth:Keys config into the database (if not already present).
    /// Called lazily on first store access.
    /// </summary>
    private async Task EnsureSeededAsync(CancellationToken ct = default)
    {
        if (_seeded) return;
        _seeded = true;

        var section = _configuration.GetSection("Tsak:Auth:Keys");
        if (!section.Exists()) return;

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
            await SaveAsync(record, ct);
            _logger.LogInformation("Seeded API key '{Name}' (id={Id}) from config", record.Name, record.Id);
        }
    }

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
            .WhereRedb(o => o.ValueString == keyHash)
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
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<ApiKeyProps>()
            .WhereRedb(o => o.ValueString == record.KeyHash)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            ApplyRecord(existing, record);
            await redb.SaveAsync(existing);
        }
        else
        {
            var obj = new RedbObject<ApiKeyProps>
            {
                name = record.Name,
                value_string = record.KeyHash,
                value_long = record.Revoked ? 1 : 0,
                note = record.Id,
                Props = ToProps(record)
            };
            await redb.SaveAsync(obj);
        }
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        // Load all and find by Id (stored in Props.Id via note field)
        var all = await redb.Query<ApiKeyProps>().ToListAsync();
        var obj = all.FirstOrDefault(o => o.note == id);
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
            Id = obj.note ?? "",
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
        Name = record.Name,
        UserId = record.UserId,
        Roles = record.Roles,
        CreatedAt = record.CreatedAt,
        ExpiresAt = record.ExpiresAt,
        Revoked = record.Revoked
    };

    private static void ApplyRecord(RedbObject<ApiKeyProps> obj, ApiKeyRecord record)
    {
        obj.name = record.Name;
        obj.note = record.Id;
        obj.value_long = record.Revoked ? 1 : 0;
        obj.Props = ToProps(record);
    }
}
