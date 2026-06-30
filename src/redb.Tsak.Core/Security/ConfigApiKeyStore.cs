using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Security;

/// <summary>
/// API key store backed by IConfiguration (appsettings).
/// Reads pre-hashed keys from Tsak:Auth:Keys[] section.
/// Suitable for standalone / headless deployments without a database.
///
/// Config format:
/// <code>
/// "Tsak": {
///   "Auth": {
///     "Keys": [
///       { "Id": "key1", "KeyHash": "A1B2...", "Name": "admin", "Roles": "admin", "UserId": "u1" }
///     ]
///   }
/// }
/// </code>
/// </summary>
public sealed class ConfigApiKeyStore : IApiKeyStore
{
    private readonly List<ApiKeyRecord> _keys;

    public ConfigApiKeyStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _keys = [];
        var section = configuration.GetSection("Tsak:Auth:Keys");
        if (!section.Exists()) return;

        foreach (var child in section.GetChildren())
        {
            var id = child["Id"];
            var keyHash = child["KeyHash"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(keyHash)) continue;

            _keys.Add(new ApiKeyRecord
            {
                Id = id,
                KeyHash = keyHash,
                Name = child["Name"] ?? "",
                UserId = child["UserId"],
                Roles = child["Roles"] ?? "",
                Revoked = bool.TryParse(child["Revoked"], out var r) && r,
                ExpiresAt = DateTimeOffset.TryParse(child["ExpiresAt"], out var exp) ? exp : null,
                CreatedAt = DateTimeOffset.TryParse(child["CreatedAt"], out var cr) ? cr : DateTimeOffset.UtcNow
            });
        }
    }

    public Task<ApiKeyRecord?> GetByHashAsync(string keyHash, CancellationToken ct = default)
    {
        var record = _keys.Find(k => string.Equals(k.KeyHash, keyHash, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<ApiKeyRecord>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiKeyRecord>>(_keys.AsReadOnly());

    public Task SaveAsync(ApiKeyRecord record, CancellationToken ct = default)
    {
        var idx = _keys.FindIndex(k => k.Id == record.Id);
        if (idx >= 0)
            _keys[idx] = record;
        else
            _keys.Add(record);
        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        var idx = _keys.FindIndex(k => k.Id == id);
        if (idx < 0) return Task.FromResult(false);
        _keys[idx] = _keys[idx] with { Revoked = true };
        return Task.FromResult(true);
    }
}
