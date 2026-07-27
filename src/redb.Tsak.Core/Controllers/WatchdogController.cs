using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Monitoring.Alerts;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// REST API for the Route Watchdog: status, alerts, enable/disable, alert-delivery test.
/// </summary>
[Route("/api/watchdog")]
public class WatchdogController : RedbController
{
    private RouteWatchdogService GetWatchdog() => Context.GetService<RouteWatchdogService>()
        ?? throw new InvalidOperationException("RouteWatchdogService not registered in context");

    private AlertDispatcher? GetDispatcher() => Context.GetService<AlertDispatcher>();

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

    /// <summary>Alert-delivery status: is delivery active, and which channels are enabled.</summary>
    [HttpGet("/alerts/status")]
    public object GetAlertStatus()
    {
        var alerts = GetDispatcher();
        return new AlertDeliveryStatus
        {
            Active = alerts?.IsActive ?? false,
            Channels = alerts?.EnabledChannels ?? []
        };
    }

    /// <summary>
    /// Sends a synthetic alert through every enabled channel and reports the per-channel outcome —
    /// so an operator can verify webhook / Telegram / email / endpoint configuration without waiting
    /// for a real hung exchange. Bypasses the dedup window.
    /// </summary>
    [HttpPost("/test-alert")]
    [AuditAdminAction(ActionName = "WatchdogTestAlert")]
    public async Task<object> TestAlert()
    {
        var alerts = GetDispatcher();
        if (alerts is null || !alerts.IsActive)
        {
            return new AlertTestResult
            {
                Delivered = false,
                Message = "Alert delivery is not active. Enable it and at least one channel under Tsak:Watchdog:Alerts."
            };
        }

        var probe = new AlertNotification
        {
            Level = "Test",
            Title = "Tsak alert delivery test",
            NodeId = Environment.MachineName,
            Timestamp = DateTimeOffset.UtcNow
        };

        var results = await alerts.TestAsync(probe, CancellationToken.None);
        return new AlertTestResult
        {
            Delivered = results.All(r => r.Success),
            Channels = results.Select(r => new AlertChannelOutcome
            {
                Channel = r.Channel,
                Success = r.Success,
                Error = r.Error
            }).ToList()
        };
    }
}
