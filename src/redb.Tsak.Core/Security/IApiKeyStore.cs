namespace redb.Tsak.Core.Security;

/// <summary>
/// Persistent store for API keys. Implementations: config-based, redb EAV-based, etc.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Find a key record by its hash. Returns null if not found.</summary>
    Task<ApiKeyRecord?> GetByHashAsync(string keyHash, CancellationToken ct = default);

    /// <summary>Get all key records (for admin listing).</summary>
    Task<IReadOnlyList<ApiKeyRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Save (insert or update) a key record.</summary>
    Task SaveAsync(ApiKeyRecord record, CancellationToken ct = default);

    /// <summary>Revoke a key by its Id.</summary>
    Task<bool> RevokeAsync(string id, CancellationToken ct = default);
}
