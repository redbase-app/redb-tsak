namespace redb.Tsak.Core.Security;

/// <summary>
/// Represents a stored API key with associated metadata.
/// The <see cref="KeyHash"/> stores HMAC-SHA256(key) — the raw key is NEVER persisted.
/// </summary>
public sealed record ApiKeyRecord
{
    /// <summary>Unique identifier for this key (for revocation / listing).</summary>
    public required string Id { get; init; }

    /// <summary>HMAC-SHA256 hash of the API key. Raw key is never stored.</summary>
    public required string KeyHash { get; init; }

    /// <summary>Display name for this key (e.g., "ci-deploy", "admin-portal").</summary>
    public string Name { get; init; } = "";

    /// <summary>User ID the key belongs to (nullable in standalone mode).</summary>
    public string? UserId { get; init; }

    /// <summary>Comma-separated roles (e.g., "admin", "reader", "operator").</summary>
    public string Roles { get; init; } = "";

    /// <summary>When the key was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional expiration. Null = never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Whether this key has been revoked.</summary>
    public bool Revoked { get; init; }

    /// <summary>Checks if the key is currently valid (not expired, not revoked).</summary>
    public bool IsValid => !Revoked && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);

    /// <summary>Parsed role set for authorization checks.</summary>
    public IReadOnlySet<string> GetRoles() =>
        string.IsNullOrWhiteSpace(Roles)
            ? (IReadOnlySet<string>)new HashSet<string>()
            : new HashSet<string>(Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}
