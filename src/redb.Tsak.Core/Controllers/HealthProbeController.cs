using Microsoft.Extensions.Configuration;
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
/// GET /api/health/deploy   — hot swap a new package (200) or deploy it cold (409)?
/// GET /api/health/deploy/cluster — the same for every alive node of the cluster.
///
/// For rich health data (CLI/Web), use GET /api/system/health instead.
/// </summary>
[Route("/api/health")]
[NoRoleRequired] // technical endpoints: kubelet and load balancers must never get a 403
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
        // No context manager yet, no contexts loaded, or the cluster coordinator still syncing: still starting.
        if (StartupProbe.Pending(Context.GetService<ITsakContextManager>(), Context.GetService<HealthCheckService>()) is { } pending)
        {
            ApiResponse.ServiceUnavailable(Exchange, pending);
            Exchange.Stop();
            return null!;
        }

        return new { status = "ok" };
    }

    /// <summary>
    /// Deploy advice for this node: 200 with verdict "hot-swap" (drop the package into modules/ of the running node),
    /// 409 with "cold-deploy" (cordon, stop, replace the packages, start, wait for ready, uncordon). Advises only.
    /// </summary>
    [HttpGet("/deploy")]
    public object GetDeployAdvice()
    {
        var advisor = Context.GetService<DeployAdvisor>();
        if (advisor is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "Deploy advice not available");
            Exchange.Stop();
            return null!;
        }

        var advice = advisor.Advise();
        return Answer(advice, advice.Verdict);
    }

    /// <summary>
    /// Deploy advice for every alive node of the cluster, asked from any node: 200 only when every node says "hot-swap",
    /// 409 otherwise. Outside a cluster it lists this node alone.
    /// </summary>
    [HttpGet("/deploy/cluster")]
    public async Task<object> GetClusterDeployAdvice()
    {
        var advisor = Context.GetService<DeployAdvisor>();
        if (advisor is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "Deploy advice not available");
            Exchange.Stop();
            return null!;
        }

        var self = advisor.Advise();
        IReadOnlyList<DeployAdviceResponse> nodes = Context.GetService<IClusterDeployAdvisor>() is { } cluster
            ? await cluster.AdviseAllAsync(self).ConfigureAwait(false)
            : [self];
        var verdict = nodes.All(n => n.Verdict == DeployVerdicts.HotSwap) ? DeployVerdicts.HotSwap : DeployVerdicts.ColdDeploy;
        return Answer(new ClusterDeployAdviceResponse { Verdict = verdict, AnsweredBy = self.NodeId, Nodes = nodes }, verdict);
    }

    /// <summary>200 for a hot swap; 409 with the same body for a cold deploy, so the status code alone carries the verdict.</summary>
    private object Answer(object body, string verdict)
    {
        if (verdict == DeployVerdicts.HotSwap)
            return body;
        ApiResponse.Respond(Exchange, 409, body);
        Exchange.Stop();
        return null!;
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

        // Degraded is 200 by default (the node serves, just slower). Some deployments prefer a
        // stricter readiness that also fails on Degraded — opt in via Tsak:Health:DegradedNotReady.
        var degradedNotReady = Context.GetService<IConfiguration>()
            ?.GetValue("Tsak:Health:DegradedNotReady", false) ?? false;

        var notReady = eval.Status == HealthStatus.Unhealthy
                       || (degradedNotReady && eval.Status == HealthStatus.Degraded);

        if (notReady)
        {
            ApiResponse.ServiceUnavailable(Exchange, eval.Description);
            Exchange.Stop();
            return null!;
        }

        return new { status = "ok" };
    }
}
