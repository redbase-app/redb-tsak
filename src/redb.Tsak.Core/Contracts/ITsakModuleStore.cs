namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Module metadata stored in module store.
/// </summary>
public record TsakModuleRecord
{
    public required string ModuleName { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public TsakModuleStatus Status { get; init; } = TsakModuleStatus.Discovered;
    public string? AssemblyPath { get; init; }
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUpdatedAt { get; init; }
}

/// <summary>
/// Persistent store for module metadata.
/// InMemory implementation for Standalone mode, redb-backed for Single/Cluster modes.
/// </summary>
public interface ITsakModuleStore
{
    Task<IReadOnlyList<TsakModuleRecord>> GetAllAsync();
    Task<TsakModuleRecord?> GetAsync(string moduleName);
    Task SaveAsync(TsakModuleRecord record);
    Task RemoveAsync(string moduleName);
    Task SaveBatchAsync(IEnumerable<TsakModuleRecord> records);
}
