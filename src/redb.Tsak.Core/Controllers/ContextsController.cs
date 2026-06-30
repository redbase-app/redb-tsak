using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Security;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Context lifecycle management: list, start, stop, restart, remove.
/// Route-level operations are in <see cref="RoutesController"/>.
/// </summary>
[Route("/api/contexts")]
public class ContextsController : RedbController
{
    private ITsakContextManager GetManager() => Context.GetService<ITsakContextManager>()
        ?? throw new InvalidOperationException("ITsakContextManager not registered in context");

    private ContextInfoCollector GetCollector() => Context.GetService<ContextInfoCollector>()
        ?? new ContextInfoCollector(GetManager());

    [HttpGet("")]
    public object ListContexts()
    {
        var mgr = GetManager();
        return mgr.GetAllContexts().Select(c =>
        {
            var routes = (c.Context as RouteContext)?.Routes;
            return new ContextSummary
            {
                Name = c.Name,
                Status = c.Context.IsStarted ? "Running" : "Stopped",
                EndpointCount = c.Context.GetEndpoints()?.Count ?? 0,
                IsAnonymous = c.Name.Contains("_dyn_", StringComparison.Ordinal),
                RouteCount = routes?.Count ?? 0
            };
        }).ToList();
    }

    [HttpGet("/{name}")]
    public object? GetContext([FromRoute("name")] string name)
    {
        var collector = GetCollector();
        var detail = collector.Collect(name);
        if (detail is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        return detail;
    }

    [HttpPost("/{name}/start")]
    [AuditAdminAction(ActionName = "StartContext", TargetParam = "name")]
    public async Task<object?> StartContext([FromRoute("name")] string name)
    {
        var mgr = GetManager();
        if (mgr.GetContext(name) is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        await mgr.StartContextAsync(name, ignoreAutoStart: true);
        return new ContextActionResponse { Name = name, Status = "Running" };
    }

    [HttpPost("/{name}/stop")]
    [AuditAdminAction(ActionName = "StopContext", TargetParam = "name")]
    public async Task<object?> StopContext(
        [FromRoute("name")] string name,
        [FromQuery("timeoutSeconds")] int? timeoutSeconds = null)
    {
        var mgr = GetManager();
        if (mgr.GetContext(name) is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        using var cts = timeoutSeconds.HasValue
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
            : null;
        var result = await mgr.StopContextAsync(name, ct: cts?.Token ?? default);
        return result;
    }

    [HttpPost("/{name}/restart")]
    [AuditAdminAction(ActionName = "RestartContext", TargetParam = "name")]
    public async Task<object?> RestartContext(
        [FromRoute("name")] string name,
        [FromQuery("timeoutSeconds")] int? timeoutSeconds = null)
    {
        var mgr = GetManager();
        if (mgr.GetContext(name) is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        using var cts = timeoutSeconds.HasValue
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
            : null;
        var result = await mgr.RestartContextAsync(name, ct: cts?.Token ?? default);
        return result;
    }

    [HttpDelete("/{name}")]
    [AuditAdminAction(ActionName = "RemoveContext", TargetParam = "name")]
    public async Task<object?> RemoveContext([FromRoute("name")] string name)
    {
        var mgr = GetManager();
        if (mgr.GetContext(name) is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        // Protect the _system context
        if (string.Equals(name, "_system", StringComparison.OrdinalIgnoreCase))
        {
            ApiResponse.BadRequest(Exchange, "Cannot remove the _system context.");
            Exchange.Stop();
            return null;
        }

        await mgr.RemoveContextAsync(name);
        return new ContextActionResponse { Name = name, Removed = true };
    }

    [HttpPost("/{name}/reset-route-states")]
    [AuditAdminAction(ActionName = "ResetRouteStates", TargetParam = "name")]
    public async Task<object?> ResetRouteStates([FromRoute("name")] string name)
    {
        var mgr = GetManager();
        if (mgr.GetContext(name) is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }

        var stateStore = Context.GetService<ITsakStateStore>();
        if (stateStore is not null)
        {
            var suspendedPrefix = RoutesController.RouteSuspendedKeyPrefix + name + ":";
            var startedPrefix = RoutesController.RouteStartedKeyPrefix + name + ":";

            var suspended = await stateStore.GetByPrefixAsync(suspendedPrefix);
            var started = await stateStore.GetByPrefixAsync(startedPrefix);

            foreach (var key in suspended.Keys)
                await stateStore.RemoveAsync(key);
            foreach (var key in started.Keys)
                await stateStore.RemoveAsync(key);
        }

        var result = await mgr.RestartContextAsync(name);
        return result;
    }

    [HttpGet("/{name}/endpoints")]
    public object? GetEndpoints([FromRoute("name")] string name)
    {
        var detail = GetCollector().Collect(name);
        if (detail is null)
        {
            ApiResponse.NotFound(Exchange, $"Context '{name}' not found.");
            Exchange.Stop();
            return null;
        }
        return detail.Endpoints;
    }
}
