namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Persistent key-value state store for context and coordinator state.
/// Used to persist autoStart flags, context config, last-known state on restart.
/// InMemory for Standalone, redb-backed for Single/Cluster.
/// </summary>
public interface ITsakStateStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
    Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix);
}
