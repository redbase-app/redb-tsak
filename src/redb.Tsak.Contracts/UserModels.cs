namespace redb.Tsak.Contracts;

public sealed record TsakUserInfo
{
    public required long Id { get; init; }
    public required string Login { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required bool Enabled { get; init; }
    public DateTimeOffset? DateRegister { get; init; }
}

public sealed record TsakCreateUserRequest
{
    public required string Login { get; init; }
    public required string Password { get; init; }
    public required string Name { get; init; }
    public string Role { get; init; } = "viewer";
}

public sealed record TsakUpdateUserRequest
{
    public string? Password { get; init; }
    public string? Name { get; init; }
    public string? Role { get; init; }
    public bool? Enabled { get; init; }
}

public sealed record TsakUserActionResponse
{
    public required string Login { get; init; }
    public required string Action { get; init; }
    public bool Success { get; init; } = true;
}
