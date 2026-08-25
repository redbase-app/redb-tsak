namespace redb.Tsak.Web.Security;

/// <summary>
/// Pure, side-effect-free security helpers for the dashboard's auth endpoints. Kept out of
/// <c>Program.cs</c> so the security-critical rules (path-traversal guard, open-redirect guard,
/// role ladder) are unit-testable in isolation.
/// </summary>
public static class DashboardAuth
{
    /// <summary>
    /// A log file name is safe only when it is a bare file name: no directory separators, no
    /// parent-directory segments, nothing that could escape the node's log directory.
    /// </summary>
    public static bool IsSafeLogFileName(string? filename) =>
        !string.IsNullOrWhiteSpace(filename)
        && !filename.Contains("..", StringComparison.Ordinal)
        && filename.IndexOfAny(new[] { '/', '\\' }) < 0
        && string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal);

    /// <summary>
    /// Only a local, single-slash-rooted path is a safe post-login redirect target. Absolute URLs,
    /// protocol-relative (<c>//host</c>) and back-slash tricks (<c>/\host</c>) are rejected so a
    /// crafted <c>returnUrl</c> cannot bounce the user off-site.
    /// </summary>
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
        // Reject control chars (tab/CR/LF/…): browsers strip them before parsing, so "/\t/evil.com"
        // collapses to "//evil.com" — a protocol-relative off-site redirect that slips past the checks
        // above (review: cookie-auth finding #2).
        && !ContainsControlChar(url);

    private static bool ContainsControlChar(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c))
                return true;
        return false;
    }

    /// <summary>
    /// Expands a dashboard role into the privilege ladder, so a policy that requires
    /// <c>operator</c> is satisfied by an <c>admin</c> too. Unknown/custom roles pass through by
    /// exact name only (an <c>admin</c> is not a custom <c>release-bot</c>).
    /// </summary>
    public static IEnumerable<string> ExpandRoles(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) yield break;
        var r = role.Trim().ToLowerInvariant();
        yield return r;
        if (r == "admin") { yield return "operator"; yield return "viewer"; }
        else if (r == "operator") { yield return "viewer"; }
    }
}
