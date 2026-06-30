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
