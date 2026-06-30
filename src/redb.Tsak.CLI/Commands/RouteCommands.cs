using System.CommandLine;
using System.CommandLine.Invocation;
using redb.Tsak.CLI.Rendering;

namespace redb.Tsak.CLI.Commands;

/// <summary>
/// CLI commands for route management: list, get, start, stop, inflight, force-stop.
/// </summary>
public static class RouteCommands
{
    public static Command Create()
    {
        var cmd = new Command("route", "Manage individual routes within contexts");

        var ctxArg = new Argument<string>("context", "Context name");

        // tsak route list <context>
        var listCtxArg = new Argument<string>("context", "Context name");
        var listCmd = new Command("list", "List routes in a context") { listCtxArg };
        listCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(listCtxArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var routes = await client.ListRoutesAsync(name, ct);
                r.RenderTable(routes,
                    ("RouteId", rt => rt.RouteId),
                    ("FromUri", rt => rt.FromUri),
                    ("Status", rt => rt.Status),
                    ("Inflight", rt => rt.InflightCount.ToString()),
                    ("AutoStart", rt => rt.AutoStart ? "Yes" : "No"));
            });
        });

        // tsak route get <context> <routeId>
        var getCtxArg = new Argument<string>("context", "Context name");
        var getRouteArg = new Argument<string>("routeId", "Route identifier");
        var getCmd = new Command("get", "Get route details") { getCtxArg, getRouteArg };
        getCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(getCtxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(getRouteArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var detail = await client.GetRouteAsync(name, routeId, ctx.GetCancellationToken());
            r.RenderDetail(
                ("RouteId", detail.RouteId),
                ("FromUri", detail.FromUri),
                ("Status", detail.Status),
                ("AutoStart", detail.AutoStart ? "Yes" : "No"),
                ("Inflight", detail.InflightCount.ToString()),
                ("Endpoints", detail.Endpoints.Length.ToString()));
        });

        // tsak route start <context> <routeId>
        var startCtxArg = new Argument<string>("context", "Context name");
        var startRouteArg = new Argument<string>("routeId", "Route identifier");
        var startCmd = new Command("start", "Start a route") { startCtxArg, startRouteArg };
        startCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(startCtxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(startRouteArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.StartRouteAsync(name, routeId, ctx.GetCancellationToken());
            r.Success($"Route '{result.RouteId}' → {result.Status}");
        });

        // tsak route stop <context> <routeId> [--timeout N]
        var stopCtxArg = new Argument<string>("context", "Context name");
        var stopRouteArg = new Argument<string>("routeId", "Route identifier");
        var timeoutOpt = new Option<int?>("--timeout", "Drain timeout in seconds");
        var stopCmd = new Command("stop", "Stop a route with graceful drain") { stopCtxArg, stopRouteArg, timeoutOpt };
        stopCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(stopCtxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(stopRouteArg);
            var timeout = ctx.ParseResult.GetValueForOption(timeoutOpt);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.StopRouteAsync(name, routeId, timeout, ctx.GetCancellationToken());
            r.Success($"Route '{result.RouteId}' → {result.Status}");
        });

        // tsak route inflight <context> [routeId]
        var inflightCtxArg = new Argument<string>("context", "Context name");
        var inflightRouteArg = new Argument<string?>("routeId", () => null!, "Route identifier (optional, all routes if omitted)");
        var inflightCmd = new Command("inflight", "Browse in-flight exchanges") { inflightCtxArg, inflightRouteArg };
        inflightCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(inflightCtxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(inflightRouteArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            await Program.RunOrWatch(ctx, async ct =>
            {
                var exchanges = routeId is not null
                    ? await client.GetRouteInflightAsync(name, routeId, ct)
                    : await client.GetContextInflightAsync(name, ct);
                r.RenderTable(exchanges,
                    ("ExchangeId", e => e.ExchangeId),
                    ("RouteId", e => e.RouteId),
                    ("Elapsed(ms)", e => e.ElapsedMs.ToString("F0")),
                    ("Thread", e => e.ThreadId.ToString()),
                    ("From", e => e.FromEndpoint ?? "—"));
            });
        });

        // tsak route force-stop <context> <routeId> [--yes]
        var forceCtxArg = new Argument<string>("context", "Context name");
        var forceRouteArg = new Argument<string>("routeId", "Route identifier");
        var forceCmd = new Command("force-stop", "Force-stop a route (no drain)") { forceCtxArg, forceRouteArg };
        forceCmd.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(forceCtxArg);
            var routeId = ctx.ParseResult.GetValueForArgument(forceRouteArg);
            Program.ConfirmOrAbort(ctx, $"Force-stop route '{routeId}' in context '{name}'?");
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.ForceStopRouteAsync(name, routeId, ctx.GetCancellationToken());
            r.Success($"Route '{result.RouteId}' → {result.Status} (force-stopped)");
        });

        cmd.AddCommand(listCmd);
        cmd.AddCommand(getCmd);
        cmd.AddCommand(startCmd);
        cmd.AddCommand(stopCmd);
        cmd.AddCommand(inflightCmd);
        cmd.AddCommand(forceCmd);
        return cmd;
    }
}
