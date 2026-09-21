using redb.Tsak.Contracts;

namespace redb.Tsak.Web.Services;

/// <summary>
/// One thing on a node an operator should look at, in the form the overview renders: a level that decides the
/// badge, a headline, a detail line, and — when the thing is a route — where to go to act on it.
/// </summary>
/// <param name="Level">"error" or "warning": the badge variant, and the sort key.</param>
/// <param name="Icon">Name of the icon shown next to the headline.</param>
/// <param name="Title">What happened, in one line.</param>
/// <param name="Detail">The numbers or the message behind it.</param>
/// <param name="ContextName">Context of the route this is about, when it is about a route.</param>
/// <param name="RouteId">Route this is about, when it is about a route.</param>
public sealed record AttentionItem(
    string Level,
    string Icon,
    string Title,
    string Detail,
    string? ContextName = null,
    string? RouteId = null);

/// <summary>
/// Turns a node's dashboard snapshot into the list of things that need an operator. Kept out of the page so
/// the rules can be tested: what counts as needing attention is the whole content of the overview, and an
/// empty list is a real answer — the node is fine — not a missing one.
/// </summary>
public static class NodeAttention
{
    /// <summary>The error level, first in the list and shown in the error colour.</summary>
    public const string Error = "error";

    /// <summary>The warning level: worth knowing, not yet broken.</summary>
    public const string Warning = "warning";

    /// <summary>
    /// Collects everything worth an operator's attention from one poll of a node. A null snapshot means the
    /// node was not reached at all, which the page reports by itself, so nothing is invented here.
    /// </summary>
    public static IReadOnlyList<AttentionItem> Collect(DashboardSnapshot? snapshot, IReadOnlyList<ModuleInfo>? modules)
    {
        var items = new List<AttentionItem>();
        if (snapshot is not null)
        {
            items.AddRange(snapshot.ActiveAlerts.Select(FromAlert));
            items.AddRange(snapshot.ErrorProneRoutes.Select(FromErrors));
            items.AddRange(snapshot.SheddingRoutes.Select(FromShedding));

            var stopped = snapshot.TotalContexts - snapshot.RunningContexts;
            if (stopped > 0)
            {
                items.Add(new AttentionItem(Warning, "route",
                    $"{stopped} of {snapshot.TotalContexts} contexts are stopped",
                    "A stopped context runs none of its routes. The Contexts tab says which ones."));
            }
        }

        if (modules is not null)
            items.AddRange(modules.Where(NeedsAttention).Select(FromModule));

        // Errors before warnings, order inside a level as collected: alerts, then routes, then modules.
        return items.OrderBy(i => i.Level == Error ? 0 : 1).ToArray();
    }

    /// <summary>A hung exchange is an error, a suspected one is a warning — that is the watchdog's own wording.</summary>
    private static AttentionItem FromAlert(WatchdogAlert alert) => new(
        alert.Level.Equals("Hung", StringComparison.OrdinalIgnoreCase) ? Error : Warning,
        "shield",
        $"Exchange {alert.Level.ToLowerInvariant()} in route '{alert.RouteId}'",
        $"{alert.ElapsedSeconds:F0}s in flight, exchange {alert.ExchangeId}, detected {alert.DetectedAt:HH:mm:ss}",
        alert.ContextName,
        alert.RouteId);

    private static AttentionItem FromErrors(RouteMetricsSummary route) => new(
        Error,
        "alert-triangle",
        $"Route '{route.RouteId}' reported {route.Errors} error{(route.Errors == 1 ? "" : "s")}",
        route.LastError is { Length: > 0 } last
            ? $"Last: {last}"
            : "No message was recorded with the last error.",
        route.ContextName,
        route.RouteId);

    /// <summary>
    /// Shedding is deliberately not an error: a route rejecting at its concurrency ceiling is working as
    /// configured and under-provisioned, and the answer is a limit or a node, not a bug hunt.
    /// </summary>
    private static AttentionItem FromShedding(RouteMetricsSummary route) => new(
        Warning,
        "activity",
        $"Route '{route.RouteId}' shed {route.Rejected} request{(route.Rejected == 1 ? "" : "s")}",
        "The route is at its admission limit: raise the limit or add nodes.",
        route.ContextName,
        route.RouteId);

    private static bool NeedsAttention(ModuleInfo module) =>
        !module.Status.Equals("Initialized", StringComparison.OrdinalIgnoreCase)
        && !module.Status.Equals("Loaded", StringComparison.OrdinalIgnoreCase);

    private static AttentionItem FromModule(ModuleInfo module) => new(
        module.Status.Equals("Error", StringComparison.OrdinalIgnoreCase) ? Error : Warning,
        "package",
        $"Module '{module.ModuleName}' is {module.Status}",
        module.CanInitialize
            ? "Its routes are not running. The Modules tab has the details."
            : "The module reports it cannot initialize on this node.");
}
