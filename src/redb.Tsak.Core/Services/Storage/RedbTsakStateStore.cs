using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// Props class for state store entries. Scheme is auto-created from type name.
/// Uses <c>value_string</c> as key for fast <c>WhereRedb</c> lookups.
/// </summary>
public class TsakStateProps
{
    /// <summary>State value (stored in Props for typed access; also mirrored to note).</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Redb-backed state store. Persists key-value state across restarts.
/// Each entry is a <see cref="RedbObject{TsakStateProps}"/> with <c>value_string</c> = key.
/// </summary>
public class RedbTsakStateStore : ITsakStateStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RedbTsakStateStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string?> GetAsync(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueString == key)
            .FirstOrDefaultAsync();

        return obj?.Props?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueString == key)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            existing.Props!.Value = value;
            existing.note = value;
            await redb.SaveAsync(existing);
        }
        else
        {
            var obj = new RedbObject<TsakStateProps>
            {
                name = key,
                value_string = key,
                note = value,
                Props = new TsakStateProps { Value = value }
            };
            await redb.SaveAsync(obj);
        }
    }

    public async Task RemoveAsync(string key)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<TsakStateProps>()
            .WhereRedb(o => o.ValueString == key)
            .FirstOrDefaultAsync();

        if (existing is not null)
            await redb.DeleteAsync(existing);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        // WhereRedb doesn't support StartsWith on ValueString,
        // so load all state entries and filter in memory.
        // State store is small (tens of entries), this is acceptable.
        var all = await redb.Query<TsakStateProps>().ToListAsync();

        return all
            .Where(o => o.ValueString?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(
                o => o.ValueString!,
                o => o.Props?.Value ?? string.Empty);
    }
}
