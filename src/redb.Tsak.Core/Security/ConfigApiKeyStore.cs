using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <param name="configuration">Host configuration carrying <c>Tsak:Auth:Keys</c>.</param>
    /// <param name="logger">
    /// Takes the reader's report on entries it had to refuse. Optional so a host that builds the store by hand
    /// keeps working; in DI the logger is always there.
    /// </param>
    public ConfigApiKeyStore(IConfiguration configuration, ILogger<ConfigApiKeyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _keys = [.. ApiKeyConfigReader.Read(configuration, logger ?? NullLogger<ConfigApiKeyStore>.Instance)];
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
