using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Aggregated dashboard snapshot endpoint for the web UI.
/// Combines system metrics, context summaries, top routes, and watchdog alerts.
/// </summary>
[Route("/api/dashboard")]
public class DashboardController : RedbController
{
    private ITsakContextManager GetManager() => Context.GetService<ITsakContextManager>()
        ?? throw new InvalidOperationException("ITsakContextManager not registered");

    private ContextInfoCollector GetCollector() => Context.GetService<ContextInfoCollector>()
        ?? new ContextInfoCollector(GetManager());

    [HttpGet("")]
    public object GetDashboard()
    {
        var mgr = GetManager();
        var collector = GetCollector();
        var metricsService = Context.GetService<MetricsService>();
        var watchdog = Context.GetService<RouteWatchdogService>();

        var contexts = mgr.GetAllContexts();
        var allRouteMetrics = collector.CollectAllRouteMetrics();

        // System metrics
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

        // total inflight
        var totalInflight = 0;
        foreach (var (_, ctx) in contexts)
        {
            if (ctx is RouteContext routeCtx)
                totalInflight += routeCtx.InflightRepository.Count;
        }

        return new DashboardSnapshot
        {
            TotalContexts = contexts.Count,
            RunningContexts = contexts.Count(c => c.Context.IsStarted),
            TotalInflight = totalInflight,
            BusiestRoutes = allRouteMetrics
                .OrderByDescending(r => r.ThroughputPerSecond)
                .Take(5)
                .ToList(),
            ErrorProneRoutes = allRouteMetrics
                .Where(r => r.Errors > 0)
                .OrderByDescending(r => r.Errors)
                .Take(5)
                .ToList(),
            ActiveAlerts = watchdog?.GetState()?.ActiveAlerts ?? [],
            SystemMetrics = sysMetrics
        };
    }
}
