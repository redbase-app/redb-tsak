using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Core;
using redb.Core.Exceptions;
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

    /// <summary>
    /// Cluster the record belongs to. Null on a record written before cluster isolation (4.0.x and
    /// older): its key is the bare module name, and a store still lists it until its own cluster
    /// saves or removes that module.
    /// </summary>
    public string? ClusterName { get; set; }
}

/// <summary>
/// Redb-backed module store. Persists module metadata across restarts and cluster nodes.
/// The key lives in <c>ValueUnique</c>: one record per module is enforced by the
/// database, and <c>SaveByUniqueAsync</c> makes <see cref="SaveAsync"/> a true upsert —
/// two nodes discovering the same module concurrently converge on one record.
/// The key is <c>"{cluster}:{module}"</c> (<see cref="TsakStorageScope"/>): several clusters on one
/// database keep their own record of an equally named module. A record written before cluster
/// isolation (bare module name, no <see cref="TsakModuleProps.ClusterName"/>) is still read until
/// this cluster saves the module; removing a module removes that old record too.
/// <c>value_string</c> still mirrors the name for dashboard visibility.
/// Every operation awaits a one-time ensure step (scheme sync + legacy backfill), so hosts
/// without TsakHostedService and reads racing its startup see pre-V4 rows
/// (review 2026-09-02, В4).
/// </summary>
public class RedbTsakModuleStore : ITsakModuleStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _clusterName;
    private readonly string _keyPrefix;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private volatile bool _ensured;

    public RedbTsakModuleStore(
        IServiceScopeFactory scopeFactory,
        ILogger<RedbTsakModuleStore>? logger = null,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<RedbTsakModuleStore>.Instance;
        _clusterName = TsakStorageScope.ClusterName(configuration);
        _keyPrefix = _clusterName + ":";
    }

    private string KeyOf(string moduleName) => _keyPrefix + moduleName;

    public async Task<IReadOnlyList<TsakModuleRecord>> GetAllAsync()
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var clusterName = _clusterName;
        var objects = await redb.Query<TsakModuleProps>()
            .Where(p => p.ClusterName == clusterName || p.ClusterName == null)
            .ToListAsync();

        var records = objects
            .Where(o => o.Props is not null && o.Props.ClusterName == clusterName)
            .Select(ToRecord)
            .ToList();

        // Records written before cluster isolation, unless this cluster has its own record of the module.
        var own = records.Select(r => r.ModuleName).ToHashSet(StringComparer.Ordinal);
        records.AddRange(objects
            .Where(o => o.Props is not null && o.Props.ClusterName is null && !own.Contains(o.Props.ModuleName))
            .Select(ToRecord));

        return records;
    }

    public async Task<TsakModuleRecord?> GetAsync(string moduleName)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var obj = await FindAsync(redb, KeyOf(moduleName)) ?? await FindLegacyAsync(redb, moduleName);
        return obj is not null ? ToRecord(obj) : null;
    }

    public async Task SaveAsync(TsakModuleRecord record)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var props = ToProps(record);
        props.ClusterName = _clusterName;

        // The Remove+Save+Save interleave (review С2) is covered by the core since BR-8 closed:
        // SaveByUniqueAsync retries an ObjectKey race onto the current winner itself.
        await redb.SaveByUniqueAsync(new RedbObject<TsakModuleProps>
        {
            name = record.ModuleName,
            ValueUnique = KeyOf(record.ModuleName),
            value_string = record.ModuleName,
            value_long = (long)record.Status,
            note = record.Description,
            Props = props
        });
    }

    public async Task RemoveAsync(string moduleName)
    {
        await EnsureReadyAsync();
        using var scope = _scopeFactory.CreateScope();
        var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();

        var existing = await FindAsync(redb, KeyOf(moduleName));
        if (existing is not null)
            await redb.DeleteAsync(existing);

        // The record written before cluster isolation goes too — otherwise the read fallback would
        // bring the removed module back.
        var legacy = await FindLegacyAsync(redb, moduleName);
        if (legacy is not null)
            await redb.DeleteAsync(legacy);
    }

    public async Task SaveBatchAsync(IEnumerable<TsakModuleRecord> records)
    {
        foreach (var record in records)
            await SaveAsync(record);
    }

    private static async Task<RedbObject<TsakModuleProps>?> FindAsync(IRedbService redb, string stored) =>
        await redb.Query<TsakModuleProps>()
            .WhereRedb(o => o.ValueUnique == stored)
            .FirstOrDefaultAsync();

    /// <summary>
    /// The record written before cluster isolation for <paramref name="moduleName"/>. Another
    /// cluster's key can spell the same string, so only a record without a cluster counts.
    /// </summary>
    private static async Task<RedbObject<TsakModuleProps>?> FindLegacyAsync(IRedbService redb, string moduleName)
    {
        var obj = await FindAsync(redb, moduleName);
        return obj is not null && obj.Props?.ClusterName is null ? obj : null;
    }

    /// <summary>One-time scheme sync + legacy backfill; safe under concurrency, retried on failure.</summary>
    private async Task EnsureReadyAsync()
    {
        if (_ensured) return;
        await _ensureLock.WaitAsync();
        try
        {
            if (_ensured) return;
            using var scope = _scopeFactory.CreateScope();
            var redb = scope.ServiceProvider.GetRequiredService<IRedbService>();
            await redb.SyncSchemeAsync<TsakModuleProps>();
            await BackfillLegacyAsync(redb, _logger);
            _ensured = true; // only after success — a failure retries on the next call
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// The ONE home of the module-store legacy migration rule. Called from the store's ensure
    /// step and from TsakHostedService startup. A pre-key row gets the bare-name key of a 4.0.x
    /// record and is read through the same fallback as one.
    /// </summary>
    internal static Task BackfillLegacyAsync(IRedbService redb, ILogger logger) =>
        TsakUniqueBackfill.RunAsync<TsakModuleProps>(
            redb,
            o => o.ValueString ?? o.Props?.ModuleName,
            TsakUniqueBackfill.DuplicatePolicy.DeleteLosers,
            logger);

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
}
