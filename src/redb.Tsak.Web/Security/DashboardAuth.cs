using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using redb.Tsak.Web.Pro;

namespace redb.Tsak.Web.Security;

/// <summary>
/// Pure, side-effect-free security helpers for the dashboard's auth endpoints. Kept out of
/// <c>Program.cs</c> so the security-critical rules (path-traversal guard, open-redirect guard,
/// role ladder, session revalidation) are unit-testable in isolation.
/// </summary>
public static class DashboardAuth
{
    /// <summary>How often a signed-in principal is re-checked against the account store.</summary>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);

    private const string RevalidatedAtClaim = "revalidated_at";

    /// <summary>
    /// Cookie-side half of session revalidation (review 2026-09-02, В14): every
    /// <see cref="RevalidationInterval"/> the account is re-resolved through
    /// <see cref="IAuthService.FindAsync"/> — a deleted user or a changed role rejects the
    /// principal and signs the cookie out instead of riding the 8-hour sliding expiration.
    /// </summary>
    public static async Task RevalidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var login = principal?.Identity?.Name;
        if (principal is null || string.IsNullOrEmpty(login))
        {
            ctx.RejectPrincipal();
            return;
        }

        var stamp = principal.FindFirst(RevalidatedAtClaim)?.Value;
        if (stamp is not null
            && long.TryParse(stamp, out var ticks)
            && DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) < RevalidationInterval)
            return; // checked recently

        var auth = ctx.HttpContext.RequestServices.GetRequiredService<IAuthService>();
        var user = await auth.FindAsync(login);
        if (user is null || !RolesMatch(principal, user.Role))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Still valid — restamp so the next requests within the interval skip the store lookup.
        var identity = (ClaimsIdentity)principal.Identity!;
        var old = identity.FindFirst(RevalidatedAtClaim);
        if (old is not null) identity.RemoveClaim(old);
        identity.AddClaim(new Claim(RevalidatedAtClaim, DateTimeOffset.UtcNow.Ticks.ToString()));
        ctx.ReplacePrincipal(principal);
        ctx.ShouldRenew = true;
    }

    /// <summary>True when the principal's role claims equal the ladder expansion of <paramref name="role"/>.</summary>
    public static bool RolesMatch(ClaimsPrincipal principal, string? role)
    {
        var expected = ExpandRoles(role).ToHashSet(StringComparer.Ordinal);
        var actual = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        return expected.SetEquals(actual);
    }

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
