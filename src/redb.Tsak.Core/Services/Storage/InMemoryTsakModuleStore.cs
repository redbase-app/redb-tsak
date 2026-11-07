using System.Collections.Concurrent;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// In-memory module store for Standalone mode. Not persistent across restarts.
/// </summary>
public class InMemoryTsakModuleStore : ITsakModuleStore
{
    private readonly ConcurrentDictionary<string, TsakModuleRecord> _modules = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<TsakModuleRecord>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<TsakModuleRecord>>(_modules.Values.ToList());

    public Task<TsakModuleRecord?> GetAsync(string moduleName) =>
        Task.FromResult(_modules.GetValueOrDefault(moduleName));

    public Task SaveAsync(TsakModuleRecord record)
    {
        _modules[record.ModuleName] = record;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string moduleName)
    {
        _modules.TryRemove(moduleName, out _);
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(IEnumerable<TsakModuleRecord> records)
    {
        foreach (var record in records)
            _modules[record.ModuleName] = record;
        return Task.CompletedTask;
    }
}
