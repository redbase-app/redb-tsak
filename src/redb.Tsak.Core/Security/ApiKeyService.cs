using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using redb.Core.Providers;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Validates API keys against <see cref="IApiKeyStore"/>.
/// - Keys are hashed with HMAC-SHA256 before lookup.
/// - Validated results are cached for <see cref="CacheTtl"/> to avoid repeated store calls.
/// - Comparison uses constant-time equality via <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
public sealed class ApiKeyService
{
    private sealed class CacheEntry
    {
        public required ApiKeyRecord Record;
        public DateTimeOffset CachedAt;
        public DateTimeOffset LastCheck;
    }

    private readonly IApiKeyStore _store;
    private readonly byte[] _hmacSecret;
    private readonly IUserProvider? _userProvider;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>Full cache lifetime for a validated key. After this the key is fully re-validated
    /// (including the linked-user enabled check). Default 5 min.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a still-cached key is cheaply re-checked against the store for revocation/expiry
    /// (review item 2.7). This bounds how long a key revoked on ANOTHER node — or directly in the
    /// database — stays accepted here, independent of the full <see cref="CacheTtl"/>. Default 30s.
    /// Set to <see cref="TimeSpan.Zero"/> to re-check on every request (no revocation window, at the
    /// cost of a store read per hit).
    /// </summary>
    public TimeSpan RevocationCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    public ApiKeyService(IApiKeyStore store, byte[] hmacSecret, IUserProvider? userProvider = null,
        Func<DateTimeOffset>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hmacSecret = hmacSecret ?? throw new ArgumentNullException(nameof(hmacSecret));
        if (_hmacSecret.Length < 16) throw new ArgumentException("HMAC secret must be at least 16 bytes.", nameof(hmacSecret));
        _userProvider = userProvider;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Hash a raw API key to its HMAC-SHA256 hex representation.
    /// This is the value stored in <see cref="ApiKeyRecord.KeyHash"/>.
    /// </summary>
    public string HashKey(string rawKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawKey);
        using var hmac = new HMACSHA256(_hmacSecret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Validate a raw API key. Returns the matching <see cref="ApiKeyRecord"/> if valid, null otherwise.
    /// Uses cache + constant-time comparison.
    /// </summary>
    public async Task<ApiKeyRecord?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;

        var keyHash = HashKey(rawKey);
        var now = _now();

        // Check cache first
        if (_cache.TryGetValue(keyHash, out var cached))
        {
            if (now - cached.CachedAt < CacheTtl)
            {
                // Within the full lifetime, but periodically re-confirm the key was not revoked or
                // expired elsewhere in the cluster (or straight in the DB) — bounds the revocation
                // window to RevocationCheckInterval instead of the whole CacheTtl.
                if (now - cached.LastCheck >= RevocationCheckInterval)
                {
                    var fresh = await _store.GetByHashAsync(keyHash, ct);
                    if (fresh is null || !fresh.IsValid)
                    {
                        _cache.TryRemove(keyHash, out _);
                        return null;
                    }
                    cached.Record = fresh;
                    cached.LastCheck = now;
                }
                return cached.Record.IsValid ? cached.Record : null;
            }
            _cache.TryRemove(keyHash, out _);
        }

        var record = await _store.GetByHashAsync(keyHash, ct);
        if (record is null) return null;

        // Constant-time comparison of the hash to prevent timing attacks
        var storedBytes = Encoding.UTF8.GetBytes(record.KeyHash);
        var computedBytes = Encoding.UTF8.GetBytes(keyHash);
        if (!CryptographicOperations.FixedTimeEquals(storedBytes, computedBytes))
            return null;

        if (!record.IsValid) return null;

        // If key is linked to a user and IUserProvider is available, verify the user is still enabled
        if (_userProvider is not null && record.UserId is not null
            && long.TryParse(record.UserId, out var userId))
        {
            var user = await _userProvider.GetUserByIdAsync(userId);
            if (user is null || !user.Enabled) return null;
        }

        // Cache the valid record
        _cache[keyHash] = new CacheEntry { Record = record, CachedAt = now, LastCheck = now };
        return record;
    }

    /// <summary>
    /// Create a new API key, store its hash, and return the raw key (shown only once).
    /// </summary>
    public async Task<(string RawKey, ApiKeyRecord Record)> CreateKeyAsync(
        string name,
        string? userId = null,
        string? roles = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        var rawKey = GenerateRawKey();
        var keyHash = HashKey(rawKey);

        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            KeyHash = keyHash,
            Name = name ?? "",
            UserId = userId,
            Roles = roles ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Revoked = false
        };

        await _store.SaveAsync(record, ct);
        return (rawKey, record);
    }

    /// <summary>Revoke a key and remove it from cache.</summary>
    public async Task<bool> RevokeKeyAsync(string id, CancellationToken ct = default)
    {
        var result = await _store.RevokeAsync(id, ct);
        if (result)
        {
            // Evict any cached entries for revoked key
            foreach (var kvp in _cache)
            {
                if (kvp.Value.Record.Id == id)
                {
                    _cache.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>List all keys (admin).</summary>
    public Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(CancellationToken ct = default)
        => _store.GetAllAsync(ct);

    /// <summary>Clear the validation cache (e.g., after bulk revocation).</summary>
    public void ClearCache() => _cache.Clear();

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return $"tsak_{Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
    }
}
