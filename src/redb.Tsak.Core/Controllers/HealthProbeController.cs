using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Lightweight health probe endpoints for Kubernetes.
/// Returns 200 (ok) or 503 (not ok) with minimal body.
/// These endpoints bypass authentication — safe for k8s probes.
///
/// GET /api/health/startup  — has the process finished initialization?
/// GET /api/health/live     — is the process alive (not hung)?
/// GET /api/health/ready    — is the process ready to serve traffic?
///
/// For rich health data (CLI/Web), use GET /api/system/health instead.
/// </summary>
[Route("/api/health")]
public class HealthProbeController : RedbController
{
    /// <summary>
    /// Startup probe: returns 200 once the process has completed initialization
    /// (contexts loaded, cluster coordinator synced if applicable).
    /// k8s uses this to know when to start liveness/readiness checks.
    /// </summary>
    [HttpGet("/startup")]
    public object GetStartup()
    {
        var ctxMgr = Context.GetService<ITsakContextManager>();
        if (ctxMgr is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "Initializing");
            Exchange.Stop();
            return null!;
        }

        var contexts = ctxMgr.GetAllContexts();

        // No contexts loaded yet — still starting
        if (contexts.Count == 0)
        {
            ApiResponse.ServiceUnavailable(Exchange, "No contexts loaded");
            Exchange.Stop();
            return null!;
        }

        // If any health contributor reports degraded/unhealthy, the system is still starting
        var healthSvc = Context.GetService<HealthCheckService>();
        if (healthSvc is not null)
        {
            var eval = healthSvc.Evaluate();
            if (eval.Checks.TryGetValue("cluster", out var clusterStatus) &&
                clusterStatus != HealthStatus.Healthy)
            {
                ApiResponse.ServiceUnavailable(Exchange, "Cluster coordinator syncing");
                Exchange.Stop();
                return null!;
            }
        }

        return new { status = "ok" };
    }

    /// <summary>
    /// Liveness probe: returns 200 if the process is alive and not deadlocked.
    /// Does NOT check module/context state — a degraded context is alive.
    /// k8s restarts the pod only if this probe fails repeatedly.
    /// </summary>
    [HttpGet("/live")]
    public object GetLive()
    {
        // The fact that we can reach this code means the event loop is responsive.
        // Check for fatal conditions only.
        var watchdog = Context.GetService<RouteWatchdogService>();
        if (watchdog is not null && watchdog.IsEnabled)
        {
            var state = watchdog.GetState();
            if (state.HungExchanges > 0)
            {
                ApiResponse.ServiceUnavailable(Exchange, "Hung exchanges detected");
                Exchange.Stop();
                return null!;
            }
        }

        return new { status = "ok" };
    }

    /// <summary>
    /// Readiness probe: returns 200 if the process is ready to accept traffic.
    /// Stricter than liveness — checks that contexts are running and healthy.
    /// k8s removes pod from service endpoints if this fails (no restart).
    /// </summary>
    [HttpGet("/ready")]
    public async Task<object> GetReady()
    {
        var healthSvc = Context.GetService<HealthCheckService>();
        if (healthSvc is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "HealthCheckService not available");
            Exchange.Stop();
            return null!;
        }

        // Use async evaluation so per-module health contributors are included.
        var eval = await healthSvc.EvaluateAsync();

        if (eval.Status == HealthStatus.Unhealthy)
        {
            ApiResponse.ServiceUnavailable(Exchange, eval.Description);
            Exchange.Stop();
            return null!;
        }

        return new { status = "ok" };
    }
}
