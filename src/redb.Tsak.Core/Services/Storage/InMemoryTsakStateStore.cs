using System.Collections.Concurrent;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// In-memory state store for Standalone mode. Not persistent across restarts.
/// Ordinal, case-sensitive keys — the <see cref="ITsakStateStore"/> contract; this class used
/// to fold case and thereby diverge from the redb-backed store (review 2026-09-02, С7).
/// </summary>
public class InMemoryTsakStateStore : ITsakStateStore
{
    private readonly ConcurrentDictionary<string, string> _state = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_state.GetValueOrDefault(key));

    public Task SetAsync(string key, string value)
    {
        _state[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _state.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix)
    {
        var result = _state
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }
}
