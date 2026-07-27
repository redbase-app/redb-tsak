using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Route-level management: list, detail, start, stop, inflight browse.
/// GET    /api/contexts/{ctx}/routes                      — list routes
/// GET    /api/contexts/{ctx}/routes/{routeId}             — route detail
/// POST   /api/contexts/{ctx}/routes/{routeId}/start       — start route
/// POST   /api/contexts/{ctx}/routes/{routeId}/stop        — stop route (optional timeout)
/// GET    /api/contexts/{ctx}/routes/{routeId}/inflight    — in-flight exchanges for route
/// GET    /api/contexts/{ctx}/inflight                     — all in-flight exchanges for context
/// </summary>
[Route("/api/contexts")]
public class RoutesController : RedbController
{
    private ITsakContextManager GetManager() => Context.GetService<ITsakContextManager>()
        ?? throw new InvalidOperationException("ITsakContextManager not registered in context");

    private ITsakStateStore? GetStateStore() => Context.GetService<ITsakStateStore>();

    internal const string RouteSuspendedKeyPrefix = "route:suspended:";
    internal const string RouteStartedKeyPrefix = "route:started:";

    internal static string RouteSuspendedKey(string contextName, string routeId) =>
        $"{RouteSuspendedKeyPrefix}{contextName}:{routeId}";

    internal static string RouteStartedKey(string contextName, string routeId) =>
        $"{RouteStartedKeyPrefix}{contextName}:{routeId}";

    private ContextInfoCollector GetCollector() => Context.GetService<ContextInfoCollector>()
        ?? new ContextInfoCollector(GetManager());

    // ═════════════════════════════════════════════════════════════════
    //  List routes
    // ═════════════════════════════════════════════════════════════════

    [HttpGet("/{name}/routes")]
    public object? ListRoutes([FromRoute("name")] string name)
    {
        var collector = GetCollector();
        var detail = collector.Collect(name);
        if (detail is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }
        return detail.Routes;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Route detail
    // ═════════════════════════════════════════════════════════════════

    [HttpGet("/{name}/routes/{routeId}")]
    public object? GetRoute([FromRoute("name")] string name, [FromRoute("routeId")] string routeId)
    {
        var collector = GetCollector();
        var route = collector.CollectRoute(name, routeId);
        if (route is null)
        {
            var mgr = GetManager();
            if (mgr.GetContext(name) is null)
                ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            else
                ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }
        return route;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Start route
    // ═════════════════════════════════════════════════════════════════

    [HttpPost("/{name}/routes/{routeId}/start")]
    [AuditAdminAction(ActionName = "StartRoute", TargetParam = "routeId")]
    public async Task<object?> StartRoute([FromRoute("name")] string name, [FromRoute("routeId")] string routeId)
    {
        var mgr = GetManager();
        var ctx = mgr.GetContext(name) as RouteContext;
        if (ctx is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        var route = ctx.GetRoute(routeId);
        if (route is null)
        {
            ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }

        await ctx.ResumeRoute(routeId);

        // Persist route state for context restart
        var stateStore = GetStateStore();
        if (stateStore is not null)
        {
            await stateStore.RemoveAsync(RouteSuspendedKey(name, routeId));
            await stateStore.SetAsync(RouteStartedKey(name, routeId), "true");
        }

        return new RouteActionResponse { RouteId = routeId, Status = route.Status.ToString() };
    }

    // ═════════════════════════════════════════════════════════════════
    //  Stop route (optional timeout via query parameter)
    // ═════════════════════════════════════════════════════════════════

    [HttpPost("/{name}/routes/{routeId}/stop")]
    [AuditAdminAction(ActionName = "StopRoute", TargetParam = "routeId")]
    public async Task<object?> StopRoute(
        [FromRoute("name")] string name,
        [FromRoute("routeId")] string routeId,
        [FromQuery("timeoutSeconds")] int? timeoutSeconds = null)
    {
        var mgr = GetManager();
        var ctx = mgr.GetContext(name) as RouteContext;
        if (ctx is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        var route = ctx.GetRoute(routeId);
        if (route is null)
        {
            ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }

        TimeSpan? timeout = timeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(timeoutSeconds.Value)
            : null;

        await ctx.StopRoute(routeId, timeout);

        // Persist suspended state so route stays stopped on context restart
        var stateStore = GetStateStore();
        if (stateStore is not null)
        {
            await stateStore.SetAsync(RouteSuspendedKey(name, routeId), "true");
            await stateStore.RemoveAsync(RouteStartedKey(name, routeId));
        }

        return new RouteActionResponse { RouteId = routeId, Status = route.Status.ToString() };
    }

    // ═════════════════════════════════════════════════════════════════
    //  Inflight exchanges — per route
    // ═════════════════════════════════════════════════════════════════

    [HttpGet("/{name}/routes/{routeId}/inflight")]
    public object? GetRouteInflight([FromRoute("name")] string name, [FromRoute("routeId")] string routeId)
    {
        var mgr = GetManager();
        var ctx = mgr.GetContext(name) as RouteContext;
        if (ctx is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        if (ctx.GetRoute(routeId) is null)
        {
            ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }

        var collector = GetCollector();
        return collector.BrowseInflight(name, routeId);
    }

    // ═════════════════════════════════════════════════════════════════
    //  Inflight exchanges — all routes in context
    // ═════════════════════════════════════════════════════════════════

    [HttpGet("/{name}/inflight")]
    public object? GetContextInflight([FromRoute("name")] string name)
    {
        var mgr = GetManager();
        var ctx = mgr.GetContext(name) as RouteContext;
        if (ctx is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        var collector = GetCollector();
        return collector.BrowseInflight(name);
    }

    // ═════════════════════════════════════════════════════════════════
    //  Per-route metrics
    // ═════════════════════════════════════════════════════════════════

    [HttpGet("/{name}/routes/{routeId}/metrics")]
    public object? GetRouteMetrics([FromRoute("name")] string name, [FromRoute("routeId")] string routeId)
    {
        var collector = GetCollector();
        var metrics = collector.CollectRouteMetrics(name, routeId);
        if (metrics is null)
        {
            var mgr = GetManager();
            if (mgr.GetContext(name) is null)
                ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            else
                ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }
        return metrics;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Force-stop route (immediate cancel, no drain)
    // ═════════════════════════════════════════════════════════════════

    [HttpPost("/{name}/routes/{routeId}/force-stop")]
    [RequiresRole(TsakRoles.Admin)] // abandons in-flight work — not an everyday operation
    [AuditAdminAction(ActionName = "ForceStopRoute", TargetParam = "routeId")]
    public async Task<object?> ForceStopRoute(
        [FromRoute("name")] string name,
        [FromRoute("routeId")] string routeId)
    {
        // Protect _system context routes from force-stop
        if (name.Equals(SystemContextBuilder.SystemContextName, StringComparison.OrdinalIgnoreCase))
        {
            ApiResponse.Forbidden(Exchange, "Cannot force-stop routes in the _system context.");
            Exchange.Stop();
            return null;
        }

        var mgr = GetManager();
        var ctx = mgr.GetContext(name) as RouteContext;
        if (ctx is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        var route = ctx.GetRoute(routeId);
        if (route is null)
        {
            ApiResponse.NotFound(Exchange, $"Route '{routeId}' not found in context '{name}'.");
            Exchange.Stop();
            return null;
        }

        // Force-stop with zero timeout — triggers immediate cancel
        await ctx.StopRoute(routeId, TimeSpan.Zero);

        // Persist suspended state so route stays stopped on context restart
        var stateStore = GetStateStore();
        if (stateStore is not null)
        {
            await stateStore.SetAsync(RouteSuspendedKey(name, routeId), "true");
            await stateStore.RemoveAsync(RouteStartedKey(name, routeId));
        }

        // Record in audit trail
        var auditService = Context.GetService<LifecycleAuditService>();
        auditService?.Record(new LifecycleEvent(
            DateTime.UtcNow, name, routeId,
            LifecycleEventType.RouteStopped,
            "Force-stopped via API (no drain)"));

        return new RouteActionResponse { RouteId = routeId, Status = route.Status.ToString() };
    }
}
