using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// Props class for module store entries.
/// Fields mirror <see cref="TsakModuleRecord"/> for typed query access.
/// </summary>
public class TsakModuleProps
{
    public string ModuleName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = [];
    public TsakModuleStatus Status { get; set; } = TsakModuleStatus.Discovered;
    public string? AssemblyPath { get; set; }
}

/// <summary>
/// Redb-backed module store. Persists module metadata across restarts and cluster nodes.
/// Each entry is a <see cref="RedbObject{TsakModuleProps}"/> with <c>value_string</c> = moduleName.
/// </summary>
public class RedbTsakModuleStore : ITsakModuleStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RedbTsakModuleStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<TsakModuleRecord>> GetAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var objects = await redb.Query<TsakModuleProps>().ToListAsync();
        return objects.Select(ToRecord).ToList();
    }

    public async Task<TsakModuleRecord?> GetAsync(string moduleName)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueString == moduleName)
            .FirstOrDefaultAsync();

        return obj is not null ? ToRecord(obj) : null;
    }

    public async Task SaveAsync(TsakModuleRecord record)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueString == record.ModuleName)
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            ApplyRecord(existing, record);
            await redb.SaveAsync(existing);
        }
        else
        {
            var obj = new RedbObject<TsakModuleProps>
            {
                name = record.ModuleName,
                value_string = record.ModuleName,
                value_long = (long)record.Status,
                note = record.Description,
                Props = ToProps(record)
            };
            await redb.SaveAsync(obj);
        }
    }

    public async Task RemoveAsync(string moduleName)
    {
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueString == moduleName)
            .FirstOrDefaultAsync();

        if (existing is not null)
            await redb.DeleteAsync(existing);
    }

    public async Task SaveBatchAsync(IEnumerable<TsakModuleRecord> records)
    {
        foreach (var record in records)
            await SaveAsync(record);
    }

    // ── Mapping helpers ──────────────────────────────────────────────

    private static TsakModuleRecord ToRecord(RedbObject<TsakModuleProps> obj)
    {
        var props = obj.Props!;
        return new TsakModuleRecord
        {
            ModuleName = props.ModuleName,
            Version = props.Version,
            Description = props.Description,
            Dependencies = props.Dependencies,
            Status = props.Status,
            AssemblyPath = props.AssemblyPath,
            RegisteredAt = obj.date_create,
            LastUpdatedAt = obj.date_modify
        };
    }

    private static TsakModuleProps ToProps(TsakModuleRecord record) => new()
    {
        ModuleName = record.ModuleName,
        Version = record.Version,
        Description = record.Description,
        Dependencies = record.Dependencies.ToList(),
        Status = record.Status,
        AssemblyPath = record.AssemblyPath
    };

    private static void ApplyRecord(RedbObject<TsakModuleProps> obj, TsakModuleRecord record)
    {
        obj.value_long = (long)record.Status;
        obj.note = record.Description;
        obj.Props = ToProps(record);
    }
}
