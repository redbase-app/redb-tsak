using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using redb.Core;
using redb.Core.Providers;
using redb.Route.Abstractions;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.RedbCore.Extensions;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Storage maintenance of the redb instances this node can reach.
/// GET  /api/storage/instances — the host's own storage and every named instance a context declares
/// GET  /api/storage/stats     — index and table statistics of one instance, plus the counter window
/// POST /api/storage/analyze   — refresh the planner statistics, of one table or of the whole database
/// <para>
/// Reads sit at operator level, not viewer: index and column names describe the application's data model, the
/// same reasoning that puts diagnostic dumps there. The refresh is an admin action and is audited — on a large
/// database it runs for minutes and it writes.
/// </para>
/// <para>
/// The instance is named in the query string, never in the path: instance names come from configuration keys
/// and a path segment cannot carry every character one might hold.
/// </para>
/// </summary>
[Route("/api/storage")]
[RequiresRole(TsakRoles.Operator)]
public class StorageController : RedbController
{
    private ITsakContextManager Manager => Context.GetService<ITsakContextManager>()
        ?? throw new InvalidOperationException("ITsakContextManager not registered in context");

    [HttpGet("/instances")]
    public object ListInstances()
    {
        var instances = new List<RedbInstanceInfo> { DescribeHostInstance() };
        instances.AddRange(Manager.GetNamedRedbInstances());
        return instances;
    }

    [HttpGet("/stats")]
    public async Task<object?> GetStats(
        [FromQuery("instance")] string? instance,
        [FromQuery("context")] string? context)
    {
        var redb = Resolve(instance, context);
        if (redb is null) return null;

        var maintenance = redb.Maintenance;
        var window = await maintenance.GetStatisticsWindowAsync();
        var tables = await maintenance.GetTableStatsAsync();
        var indexes = await maintenance.GetIndexStatsAsync();

        return new RedbStorageStats
        {
            Instance = instance ?? "",
            ContextName = context,
            CountersSince = window.CountersSince,
            IsReplica = window.IsReplica,
            Tables = tables.Select(Map).ToArray(),
            Indexes = indexes.Select(Map).ToArray()
        };
    }

    /// <summary>
    /// Refreshes the planner statistics. Without <c>table</c> the whole database is analyzed, which on a large
    /// one runs for minutes — the caller is expected to hold a control-timeout client. The table name is
    /// checked against the catalogs by the engine provider before it reaches any SQL.
    /// </summary>
    [HttpPost("/analyze")]
    [RequiresRole(TsakRoles.Admin)]
    [AuditAdminAction(ActionName = "AnalyzeStorage", TargetParam = "table")]
    public async Task<object?> Analyze(
        [FromQuery("instance")] string? instance,
        [FromQuery("context")] string? context,
        [FromQuery("table")] string? table,
        [FromQuery("schema")] string? schema)
    {
        var redb = Resolve(instance, context);
        if (redb is null) return null;

        var sw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(table))
                await redb.Maintenance.AnalyzeAsync();
            else
                await redb.Maintenance.AnalyzeTableAsync(table, string.IsNullOrWhiteSpace(schema) ? null : schema);
        }
        catch (InvalidOperationException ex)
        {
            // The provider refuses a table the catalogs do not know: a stale page, or a name that never
            // existed. That is a request problem, not a server fault.
            ApiResponse.BadRequest(Exchange, ex.Message);
            Exchange.Stop();
            return null;
        }
        sw.Stop();

        return new RedbAnalyzeResult
        {
            Instance = instance ?? "",
            Table = table,
            Schema = schema,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Resolves the instance to work with: the host's own storage when no name is given, otherwise the named
    /// instance registered on that context. Both come back scoped to this request, with a connection of their
    /// own. Writes the 404 and stops the exchange when the name resolves to nothing, and answers null.
    /// </summary>
    private IRedbService? Resolve(string? instance, string? contextName)
    {
        if (string.IsNullOrWhiteSpace(instance))
            return this.Redb();

        if (string.IsNullOrWhiteSpace(contextName))
        {
            ApiResponse.BadRequest(Exchange, "A named instance needs the context that declares it: pass 'context'.");
            Exchange.Stop();
            return null;
        }

        var context = Manager.GetContext(contextName);
        if (context is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{contextName}' not found.");
            Exchange.Stop();
            return null;
        }

        var known = Manager.GetNamedRedbInstances().Any(i =>
            string.Equals(i.Name, instance, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.ContextName, contextName, StringComparison.OrdinalIgnoreCase));

        if (!known)
        {
            ApiResponse.NotFound(Exchange, $"Context '{contextName}' has no redb instance named '{instance}'.");
            Exchange.Stop();
            return null;
        }

        // Scoped to this exchange, so concurrent requests never share the instance's connection. A
        // non-active instance opens its connection right here — that is what makes this call the one that
        // wakes it, and why the dashboard asks only when the operator asks.
        return context.GetRedbService(instance, Exchange);
    }

    /// <summary>The host's own storage, described from the configuration it was built from.</summary>
    private RedbInstanceInfo DescribeHostInstance()
    {
        var configuration = Context.GetService<IConfiguration>();
        var provider = configuration?["Tsak:Redb:Provider"];

        // The same switch the host's own wiring uses to pick its connection string, so the fingerprint of the
        // host matches the fingerprint of a named instance that points at the same database.
        var connectionString = provider?.ToLowerInvariant() switch
        {
            null => null,
            "mssql" or "sqlserver" => configuration?.GetConnectionString("MSSql"),
            "sqlite" => configuration?.GetConnectionString("Sqlite"),
            _ => configuration?.GetConnectionString("Postgres")
        };

        return RedbInstanceDescription.Describe(
            name: "",
            contextName: null,
            provider: provider,
            connectionString: connectionString,
            usePro: configuration?.GetValue("Tsak:Redb:UsePro", true) ?? true,
            isActive: true);
    }

    private static RedbTableInfo Map(TableStatistics t) => new()
    {
        Schema = t.Schema,
        Table = t.Table,
        IsRedbOwned = t.IsRedbOwned,
        EstimatedRows = t.EstimatedRows,
        DataSizeBytes = t.DataSizeBytes,
        IndexesSizeBytes = t.IndexesSizeBytes,
        DeadRows = t.DeadRows,
        LastAnalyze = t.LastAnalyze,
        LastAutoAnalyze = t.LastAutoAnalyze,
        LastVacuum = t.LastVacuum,
        HasStatistics = t.HasStatistics
    };

    private static RedbIndexInfo Map(IndexStatistics i) => new()
    {
        Schema = i.Schema,
        Table = i.Table,
        Name = i.Name,
        IsUnique = i.IsUnique,
        Columns = i.Columns.ToArray(),
        IncludedColumns = i.IncludedColumns.ToArray(),
        IsPrimaryKey = i.IsPrimaryKey,
        IsUniqueConstraint = i.IsUniqueConstraint,
        IsClustered = i.IsClustered,
        BacksForeignKey = i.BacksForeignKey,
        IsRedbOwned = i.IsRedbOwned,
        IsSystemCritical = i.IsSystemCritical,
        CriticalReason = i.CriticalReason.ToString(),
        SizeBytes = i.SizeBytes,
        EstimatedRows = i.EstimatedRows,
        Seeks = i.Seeks,
        Scans = i.Scans,
        Updates = i.Updates,
        LastUsed = i.LastUsed
    };
}
