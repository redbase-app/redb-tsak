namespace redb.Tsak.Web.Services;

/// <summary>
/// The tabs of the node page, and the rule that turns the <c>{Tab}</c> route value into one of them.
/// <para>
/// The sidebar links a node with no tab at all — <c>node/{id}</c> — so "no tab" has to mean the first tab.
/// While it meant "keep whatever is open", clicking Overview in the sidebar from any other tab left that
/// other tab on screen, and the operator had to pick Overview again in the tab strip (found live 2026-09-21).
/// </para>
/// </summary>
public static class NodeTabs
{
    /// <summary>The tabs in the order the strip shows them. The first one is what a tab-less URL selects.</summary>
    public static readonly string[] All = ["Overview", "Contexts", "Scheduler", "Modules", "Monitoring", "Logs"];

    /// <summary>The default tab: what <c>node/{id}</c> opens on.</summary>
    public static string Default => All[0];

    /// <summary>
    /// The tab a route value selects: the one it names, or the default for no value and for a value that
    /// names no tab.
    /// </summary>
    public static string Resolve(string? tab) =>
        All.FirstOrDefault(t => t.Equals(tab, StringComparison.OrdinalIgnoreCase)) ?? Default;
}
