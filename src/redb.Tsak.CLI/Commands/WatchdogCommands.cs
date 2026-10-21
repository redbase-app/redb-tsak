using System.CommandLine;
using redb.Tsak.CLI.Rendering;

namespace redb.Tsak.CLI.Commands;

/// <summary>
/// CLI commands for watchdog management: status, alerts, enable, disable.
/// </summary>
public static class WatchdogCommands
{
    public static Command Create()
    {
        var cmd = new Command("watchdog", "Manage route watchdog service");

        // tsak watchdog status [--watch]
        var statusCmd = new Command("status", "Show watchdog status");
        Program.Handle(statusCmd, async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var state = await client.GetWatchdogStatusAsync(ct);
                r.RenderDetail(
                    ("Enabled", state.Enabled ? "Yes" : "No"),
                    ("LastCheck", state.LastCheckAt.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("SuspectedExchanges", state.SuspectedExchanges.ToString()),
                    ("HungExchanges", state.HungExchanges.ToString()),
                    ("AutoRestarts", state.AutoRestartsPerformed.ToString()),
                    ("ActiveAlerts", state.ActiveAlerts.Count.ToString()));
            });
        });

        // tsak watchdog alerts [--watch]
        var alertsCmd = new Command("alerts", "List active watchdog alerts");
        Program.Handle(alertsCmd, async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var alerts = await client.GetWatchdogAlertsAsync(ct);
                r.RenderTable(alerts,
                    ("ExchangeId", a => a.ExchangeId),
                    ("RouteId", a => a.RouteId),
                    ("Context", a => a.ContextName),
                    ("Level", a => a.Level),
                    ("DetectedAt", a => a.DetectedAt.ToString("HH:mm:ss")),
                    ("Elapsed(s)", a => a.ElapsedSeconds.ToString("F1")));
            });
        });

        // tsak watchdog enable
        var enableCmd = new Command("enable", "Enable the watchdog service");
        Program.Handle(enableCmd, async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await client.EnableWatchdogAsync(ctx.CancellationToken);
            r.Success("Watchdog enabled.");
        });

        // tsak watchdog disable
        var disableCmd = new Command("disable", "Disable the watchdog service");
        Program.Handle(disableCmd, async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await client.DisableWatchdogAsync(ctx.CancellationToken);
            r.Success("Watchdog disabled.");
        });

        cmd.Add(statusCmd);
        cmd.Add(alertsCmd);
        cmd.Add(enableCmd);
        cmd.Add(disableCmd);
        return cmd;
    }
}
