namespace redb.Tsak.Contracts;

public sealed record ApiKeyCreatedResponse
{
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Roles { get; init; }
    public string? UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Message { get; init; }
}

public sealed record ApiKeyInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Roles { get; init; }
    public string? UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required bool Revoked { get; init; }
    public required bool IsValid { get; init; }
}

public sealed record ApiKeyRevokedResponse
{
    public required string Id { get; init; }
    public required bool Revoked { get; init; }
}
