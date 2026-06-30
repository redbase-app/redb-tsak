using System.CommandLine;
using System.CommandLine.Invocation;
using redb.Tsak.CLI.Rendering;

namespace redb.Tsak.CLI.Commands;

/// <summary>
/// CLI commands for diagnostics: lifecycle events, system dump, route dump, dashboard.
/// </summary>
public static class DiagnosticsCommands
{
    public static Command Create()
    {
        var cmd = new Command("diagnostics", "Diagnostics, lifecycle events, and dashboard");

        cmd.AddCommand(BuildLifecycleCommand());
        cmd.AddCommand(BuildDumpCommand());
        cmd.AddCommand(BuildRouteDumpCommand());
        cmd.AddCommand(BuildDashboardCommand());

        return cmd;
    }

    // tsak diagnostics lifecycle [--context X] [--route Y] [--type Z] [--since DT] [--limit N] [--watch]
    private static Command BuildLifecycleCommand()
    {
        var contextOpt = new Option<string?>("--context", "Filter by context name");
        var routeOpt = new Option<string?>("--route", "Filter by route identifier");
        var typeOpt = new Option<string?>("--type", "Filter by event type");
        var sinceOpt = new Option<DateTime?>("--since", "Events since this timestamp (ISO 8601)");
        var limitOpt = new Option<int?>("--limit", "Maximum number of events to return");

        var lifecycleCmd = new Command("lifecycle", "Browse lifecycle audit events")
        {
            contextOpt, routeOpt, typeOpt, sinceOpt, limitOpt
        };

        lifecycleCmd.SetHandler(async ctx =>
        {
            var context = ctx.ParseResult.GetValueForOption(contextOpt);
            var route = ctx.ParseResult.GetValueForOption(routeOpt);
            var type = ctx.ParseResult.GetValueForOption(typeOpt);
            var since = ctx.ParseResult.GetValueForOption(sinceOpt);
            var limit = ctx.ParseResult.GetValueForOption(limitOpt);

            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var events = await client.GetLifecycleEventsAsync(context, route, type, since, limit, ct);
                r.RenderTable(events,
                    ("Timestamp", e => e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("Context", e => e.ContextName),
                    ("Route", e => e.RouteId ?? "—"),
                    ("Event", e => e.EventType.ToString()),
                    ("Details", e => e.Details ?? "—"));
            });
        });

        return lifecycleCmd;
    }

    // tsak diagnostics dump
    private static Command BuildDumpCommand()
    {
        var dumpCmd = new Command("dump", "Full system diagnostic dump");
        dumpCmd.SetHandler(async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var dump = await client.GetDiagnosticDumpAsync(ctx.GetCancellationToken());

            r.RenderDetail(
                ("Timestamp", dump.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                ("Inflight", dump.InflightExchanges.Count.ToString()),
                ("Contexts", dump.Contexts.Count.ToString()),
                ("ActiveAlerts", dump.ActiveAlerts.Count.ToString()),
                ("RecentEvents", dump.RecentEvents.Count.ToString()),
                ("ThreadPool.Pending", dump.ThreadPool.PendingWorkItems.ToString()),
                ("ThreadPool.Workers", $"{dump.ThreadPool.WorkerThreadsAvailable}/{dump.ThreadPool.WorkerThreadsMax}"),
                ("ThreadPool.IO", $"{dump.ThreadPool.IoThreadsAvailable}/{dump.ThreadPool.IoThreadsMax}"),
                ("GC.Gen0", dump.GarbageCollector.Gen0Collections.ToString()),
                ("GC.Gen1", dump.GarbageCollector.Gen1Collections.ToString()),
                ("GC.Gen2", dump.GarbageCollector.Gen2Collections.ToString()),
                ("GC.Memory(MB)", dump.GarbageCollector.TotalMemoryMB.ToString("F1")));

            if (dump.SystemMetrics is { } sm)
            {
                r.RenderDetail(
                    ("CPU(%)", sm.CpuPercent.ToString("F1")),
                    ("Memory(MB)", sm.MemoryMB.ToString("F1")),
                    ("Threads", sm.ThreadCount.ToString()));
            }

            if (dump.Contexts.Count > 0)
            {
                r.RenderTable(dump.Contexts,
                    ("Context", c => c.ContextName),
                    ("Started", c => c.IsStarted ? "Yes" : "No"),
                    ("Routes", c => c.RouteCount.ToString()),
                    ("Endpoints", c => c.EndpointCount.ToString()),
                    ("Inflight", c => c.InflightCount.ToString()));
            }
        });

        return dumpCmd;
    }

    // tsak diagnostics route <context> <routeId>
    private static Command BuildRouteDumpCommand()
    {
        var ctxArg = new Argument<string>("context", "Context name");
        var routeArg = new Argument<string>("routeId", "Route identifier");
        var routeCmd = new Command("route", "Diagnostic dump for a specific route") { ctxArg, routeArg };

        routeCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(ctxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(routeArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var dump = await client.GetRouteDiagnosticsAsync(name, routeId, ctx.GetCancellationToken());

            r.RenderDetail(
                ("RouteId", dump.RouteId),
                ("FromUri", dump.FromUri),
                ("Status", dump.Status),
                ("AutoStart", dump.AutoStart ? "Yes" : "No"),
                ("Inflight", dump.InflightExchanges.Count.ToString()),
                ("RecentErrors", dump.RecentErrors.Count.ToString()),
                ("LifecycleEvents", dump.LifecycleHistory.Count.ToString()));

            if (dump.Metrics is { } m)
            {
                r.RenderDetail(
                    ("Messages", m.MessagesProcessed.ToString()),
                    ("Errors", m.Errors.ToString()),
                    ("AvgProcessing(ms)", m.AvgProcessingMs.ToString("F1")),
                    ("Throughput(/s)", m.ThroughputPerSecond.ToString("F2")),
                    ("LastActivity", m.LastActivity?.ToString("HH:mm:ss") ?? "—"));
            }

            if (dump.InflightExchanges.Count > 0)
            {
                r.RenderTable(dump.InflightExchanges,
                    ("ExchangeId", e => e.ExchangeId),
                    ("RouteId", e => e.RouteId),
                    ("Elapsed(ms)", e => e.ElapsedMs.ToString("F0")),
                    ("Thread", e => e.ThreadId.ToString()));
            }
        });

        return routeCmd;
    }

    // tsak diagnostics dashboard [--watch]
    private static Command BuildDashboardCommand()
    {
        var dashCmd = new Command("dashboard", "Live dashboard summary");
        dashCmd.SetHandler(async ctx =>
        {
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var snap = await client.GetDashboardAsync(ct);
                r.RenderDetail(
                    ("Timestamp", snap.Timestamp.ToString("HH:mm:ss")),
                    ("Contexts", $"{snap.RunningContexts}/{snap.TotalContexts}"),
                    ("Inflight", snap.TotalInflight.ToString()),
                    ("Alerts", snap.ActiveAlerts.Count.ToString()));

                if (snap.SystemMetrics is { } sm)
                {
                    r.RenderDetail(
                        ("CPU(%)", sm.CpuPercent.ToString("F1")),
                        ("Memory(MB)", sm.MemoryMB.ToString("F1")),
                        ("Threads", sm.ThreadCount.ToString()));
                }

                if (snap.BusiestRoutes.Count > 0)
                {
                    r.RenderTable(snap.BusiestRoutes,
                        ("RouteId", rt => rt.RouteId),
                        ("Status", rt => rt.Status),
                        ("Msgs", rt => rt.MessagesProcessed.ToString()),
                        ("Throughput", rt => rt.ThroughputPerSecond.ToString("F1")),
                        ("Inflight", rt => rt.InflightCount.ToString()));
                }

                if (snap.ErrorProneRoutes.Count > 0)
                {
                    r.RenderTable(snap.ErrorProneRoutes,
                        ("RouteId", rt => rt.RouteId),
                        ("Errors", rt => rt.Errors.ToString()),
                        ("LastError", rt => rt.LastError ?? "—"),
                        ("LastErrorTime", rt => rt.LastErrorTime?.ToString("HH:mm:ss") ?? "—"));
                }
            });
        });

        return dashCmd;
    }
}
