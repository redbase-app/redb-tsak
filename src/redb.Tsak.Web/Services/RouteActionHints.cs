namespace redb.Tsak.Web.Services;

/// <summary>
/// What the route buttons do, in the words an operator needs before clicking. The force-stop button in the
/// routes table carries an icon and no label at all, so without these the difference between stopping and
/// force-stopping was visible only in the confirmation dialog — after the click.
/// </summary>
public static class RouteActionHints
{
    /// <summary>Tooltip of the Start button.</summary>
    public const string Start =
        "Start the route: its consumer begins accepting exchanges again. The choice is remembered, so the route "
        + "stays started after the context restarts.";

    /// <summary>Tooltip of the Stop button.</summary>
    public const string Stop =
        "Stop the route: its consumer accepts no new exchanges, the ones in flight are allowed to finish. The "
        + "choice is remembered, so the route stays stopped after the context restarts.";

    /// <summary>Tooltip of the Force-stop button.</summary>
    public const string ForceStop =
        "Force-stop the route: the exchanges in flight are cancelled at once, not awaited. For a route that is "
        + "stuck; an exchange cancelled this way is not delivered.";
}
