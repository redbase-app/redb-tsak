using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// REST API for the Route Watchdog: status, alerts, enable/disable.
/// </summary>
[Route("/api/watchdog")]
public class WatchdogController : RedbController
{
    private RouteWatchdogService GetWatchdog() => Context.GetService<RouteWatchdogService>()
        ?? throw new InvalidOperationException("RouteWatchdogService not registered in context");

    [HttpGet("/status")]
    public object GetStatus()
    {
        return GetWatchdog().GetState();
    }

    [HttpGet("/alerts")]
    public object GetAlerts()
    {
        return GetWatchdog().GetState().ActiveAlerts;
    }

    [HttpPost("/enable")]
    [AuditAdminAction(ActionName = "WatchdogEnable")]
    public object Enable()
    {
        var watchdog = GetWatchdog();
        watchdog.Enable();
        return new { enabled = true };
    }

    [HttpPost("/disable")]
    [AuditAdminAction(ActionName = "WatchdogDisable")]
    public object Disable()
    {
        var watchdog = GetWatchdog();
        watchdog.Disable();
        return new { enabled = false };
    }
}
