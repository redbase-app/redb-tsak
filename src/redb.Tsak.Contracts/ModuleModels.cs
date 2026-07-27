namespace redb.Tsak.Contracts;

public sealed record ModuleInfo
{
    public required string ModuleName { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required bool CanInitialize { get; init; }
    public required string[] Dependencies { get; init; }
}

public sealed record ModuleRemovedResponse
{
    public required string ModuleName { get; init; }
    public required bool Removed { get; init; }
}

/// <summary>Result of a module upload or rollback.</summary>
public sealed record ModuleDeployResponse
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? ModuleName { get; init; }
    public string? Version { get; init; }
}
