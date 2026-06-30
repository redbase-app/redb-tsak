using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Collects a comprehensive diagnostic snapshot of the system state.
/// Aggregates inflight exchanges, thread pool stats, GC stats,
/// per-context summaries, watchdog alerts, and recent lifecycle events.
/// </summary>
public class DiagnosticService
{
    private readonly ITsakContextManager _contextManager;

    public DiagnosticService(ITsakContextManager contextManager)
    {
        _contextManager = contextManager;
    }

    /// <summary>
    /// Builds a full system diagnostic dump.
    /// </summary>
    public DiagnosticDump CollectSystemDump(
        MetricsService? metricsService = null,
        RouteWatchdogService? watchdog = null,
        LifecycleAuditService? auditService = null)
    {
        var contexts = _contextManager.GetAllContexts();
        var collector = new ContextInfoCollector(_contextManager);

        // Collect all inflight exchanges across all contexts
        var allInflight = new List<InflightExchangeDto>();
        var contextSummaries = new List<DiagnosticContextSummary>();

        foreach (var (name, ctx) in contexts)
        {
            var inflight = collector.BrowseInflight(name);
            if (inflight is not null)
                allInflight.AddRange(inflight);

            var routeCtx = ctx as RouteContext;
            contextSummaries.Add(new DiagnosticContextSummary
            {
                ContextName = name,
                IsStarted = ctx.IsStarted,
                RouteCount = routeCtx?.Routes.Count ?? 0,
                EndpointCount = ctx.GetEndpoints().Count,
                InflightCount = routeCtx?.InflightRepository.Count ?? 0
            });
        }

        // Thread pool stats
        ThreadPool.GetAvailableThreads(out var workerAvail, out var ioAvail);
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);

        var threadPoolInfo = new ThreadPoolDiagnostics
        {
            PendingWorkItems = ThreadPool.PendingWorkItemCount,
            WorkerThreadsAvailable = workerAvail,
            WorkerThreadsMin = workerMin,
            WorkerThreadsMax = workerMax,
            IoThreadsAvailable = ioAvail,
            IoThreadsMin = ioMin,
            IoThreadsMax = ioMax
        };

        // GC stats
        var gcInfo = new GcDiagnostics
        {
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalMemoryMB = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2)
        };

        // System metrics from MetricsService if available
        SystemMetricsSnapshot? sysMetrics = null;
        var latest = metricsService?.GetLatest();
        if (latest is not null)
        {
            sysMetrics = new SystemMetricsSnapshot
            {
                CpuPercent = latest.Cpu.ProcessUsage,
                MemoryMB = latest.Memory.WorkingSetMB,
                ThreadCount = latest.Threading.ActiveThreads,
                Gen2Collections = latest.GarbageCollector.Gen2Collections
            };
        }

        return new DiagnosticDump
        {
            InflightExchanges = allInflight,
            ThreadPool = threadPoolInfo,
            GarbageCollector = gcInfo,
            Contexts = contextSummaries,
            ActiveAlerts = watchdog?.GetState()?.ActiveAlerts ?? [],
            RecentEvents = auditService?.GetEvents(limit: 20) ?? [],
            SystemMetrics = sysMetrics
        };
    }

    /// <summary>
    /// Builds a diagnostic dump for a single route.
    /// </summary>
    public RouteDiagnosticDump? CollectRouteDump(
        string contextName,
        string routeId,
        LifecycleAuditService? auditService = null)
    {
        var ctx = _contextManager.GetContext(contextName) as RouteContext;
        if (ctx is null) return null;

        var route = ctx.GetRoute(routeId);
        if (route is null) return null;

        var collector = new ContextInfoCollector(_contextManager);
        var inflight = collector.BrowseInflight(contextName, routeId) ?? [];
        var metrics = collector.CollectRouteMetrics(contextName, routeId);

        var stats = route.Endpoint as IEndpointStatistics;
        var recentErrors = new List<string>();
        if (stats?.LastErrorMessage is { Length: > 0 } msg)
            recentErrors.Add($"[{stats.LastErrorTime:u}] {msg}");

        return new RouteDiagnosticDump
        {
            RouteId = route.RouteId,
            FromUri = route.Endpoint.Uri.ToMaskedUriString(),
            Status = route.Status.ToString(),
            AutoStart = route.AutoStart,
            InflightExchanges = inflight,
            Metrics = metrics,
            RecentErrors = recentErrors,
            LifecycleHistory = auditService?.GetEvents(routeId: routeId, limit: 20) ?? []
        };
    }
}
