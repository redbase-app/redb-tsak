using System.CommandLine;
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

        var ctxArg = new Argument<string>("context") { Description = "Context name" };

        // tsak route list <context>
        var listCtxArg = new Argument<string>("context") { Description = "Context name" };
        var listCmd = new Command("list", "List routes in a context") { listCtxArg };
        Program.Handle(listCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(listCtxArg);
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
        var getCtxArg = new Argument<string>("context") { Description = "Context name" };
        var getRouteArg = new Argument<string>("routeId") { Description = "Route identifier" };
        var getCmd = new Command("get", "Get route details") { getCtxArg, getRouteArg };
        Program.Handle(getCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(getCtxArg);
            var routeId = ctx.ParseResult.GetValue(getRouteArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var detail = await client.GetRouteAsync(name, routeId, ctx.CancellationToken);
            r.RenderDetail(
                ("RouteId", detail.RouteId),
                ("FromUri", detail.FromUri),
                ("Status", detail.Status),
                ("AutoStart", detail.AutoStart ? "Yes" : "No"),
                ("Inflight", detail.InflightCount.ToString()),
                ("Endpoints", detail.Endpoints.Length.ToString()));
        });

        // tsak route start <context> <routeId>
        var startCtxArg = new Argument<string>("context") { Description = "Context name" };
        var startRouteArg = new Argument<string>("routeId") { Description = "Route identifier" };
        var startCmd = new Command("start", "Start a route") { startCtxArg, startRouteArg };
        Program.Handle(startCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(startCtxArg);
            var routeId = ctx.ParseResult.GetValue(startRouteArg);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.StartRouteAsync(name, routeId, ctx.CancellationToken);
            r.Success($"Route '{result.RouteId}' → {result.Status}");
        });

        // tsak route stop <context> <routeId> [--timeout N]
        var stopCtxArg = new Argument<string>("context") { Description = "Context name" };
        var stopRouteArg = new Argument<string>("routeId") { Description = "Route identifier" };
        var timeoutOpt = new Option<int?>("--timeout") { Description = "Drain timeout in seconds" };
        var stopCmd = new Command("stop", "Stop a route with graceful drain") { stopCtxArg, stopRouteArg, timeoutOpt };
        Program.Handle(stopCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(stopCtxArg);
            var routeId = ctx.ParseResult.GetValue(stopRouteArg);
            var timeout = ctx.ParseResult.GetValue(timeoutOpt);
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.StopRouteAsync(name, routeId, timeout, ctx.CancellationToken);
            r.Success($"Route '{result.RouteId}' → {result.Status}");
        });

        // tsak route inflight <context> [routeId]
        var inflightCtxArg = new Argument<string>("context") { Description = "Context name" };
        var inflightRouteArg = new Argument<string?>("routeId")
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => null,
            Description = "Route identifier (optional, all routes if omitted)",
        };
        var inflightCmd = new Command("inflight", "Browse in-flight exchanges") { inflightCtxArg, inflightRouteArg };
        Program.Handle(inflightCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(inflightCtxArg);
            var routeId = ctx.ParseResult.GetValue(inflightRouteArg);
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
        var forceCtxArg = new Argument<string>("context") { Description = "Context name" };
        var forceRouteArg = new Argument<string>("routeId") { Description = "Route identifier" };
        var forceCmd = new Command("force-stop", "Force-stop a route (no drain)") { forceCtxArg, forceRouteArg };
        Program.Handle(forceCmd, async ctx =>
        {
            var name = ctx.ParseResult.GetValue(forceCtxArg);
            var routeId = ctx.ParseResult.GetValue(forceRouteArg);
            Program.ConfirmOrAbort(ctx, $"Force-stop route '{routeId}' in context '{name}'?");
            using var client = Program.CreateClient(ctx);
            var r = Program.CreateRenderer(ctx);
            var result = await client.ForceStopRouteAsync(name, routeId, ctx.CancellationToken);
            r.Success($"Route '{result.RouteId}' → {result.Status} (force-stopped)");
        });

        cmd.Add(listCmd);
        cmd.Add(getCmd);
        cmd.Add(startCmd);
        cmd.Add(stopCmd);
        cmd.Add(inflightCmd);
        cmd.Add(forceCmd);
        return cmd;
    }
}
