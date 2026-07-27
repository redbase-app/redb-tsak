using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Core;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Diagnostic endpoints: system dump and per-route diagnostics.
/// </summary>
[Route("/api/diagnostics")]
[RequiresRole(TsakRoles.Operator)] // dumps expose thread state and internals — not a read-only concern
public class DiagnosticsController : RedbController
{
    private ITsakContextManager GetManager() => Context.GetService<ITsakContextManager>()
        ?? throw new InvalidOperationException("ITsakContextManager not registered");

    /// <summary>
    /// GET /api/diagnostics/dump — full system diagnostic snapshot.
    /// </summary>
    [HttpGet("/dump")]
    public object GetSystemDump()
    {
        var mgr = GetManager();
        var diagService = new DiagnosticService(mgr);

        return diagService.CollectSystemDump(
            metricsService: Context.GetService<MetricsService>(),
            watchdog: Context.GetService<RouteWatchdogService>(),
            auditService: Context.GetService<LifecycleAuditService>());
    }

    /// <summary>
    /// GET /api/diagnostics/{contextName}/{routeId} — per-route diagnostic dump.
    /// </summary>
    [HttpGet("/{contextName}/{routeId}")]
    public object? GetRouteDiagnostics(
        [FromRoute("contextName")] string contextName,
        [FromRoute("routeId")] string routeId)
    {
        var mgr = GetManager();
        var diagService = new DiagnosticService(mgr);
        var auditService = Context.GetService<LifecycleAuditService>();

        var dump = diagService.CollectRouteDump(contextName, routeId, auditService);
        if (dump is null)
        {
            if (mgr.GetContext(contextName) is null)
                ApiResponse.NotFound(Exchange, $"Context '{contextName}' not found.");
            else
                ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{contextName}'.");
            Exchange.Stop();
            return null;
        }

        return dump;
    }
}
