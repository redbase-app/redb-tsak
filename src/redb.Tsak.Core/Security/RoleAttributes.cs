namespace redb.Tsak.Core.Security;

/// <summary>
/// Well-known Tsak roles, ordered by privilege: <see cref="Viewer"/> &lt;
/// <see cref="Operator"/> &lt; <see cref="Admin"/>. A key holding a higher role
/// satisfies a requirement for a lower one — <c>admin</c> can do everything
/// <c>operator</c> can, and so on.
/// <para>
/// Roles outside this ladder (custom roles on a key) are matched by exact name only.
/// </para>
/// </summary>
public static class TsakRoles
{
    /// <summary>Read-only access: every <c>GET</c> endpoint.</summary>
    public const string Viewer = "viewer";

    /// <summary>Day-to-day operations: context / route / scheduler lifecycle.</summary>
    public const string Operator = "operator";

    /// <summary>Full control: keys, users, module removal, force-stop, cluster surgery.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Privilege rank of a well-known role (higher wins). Returns <c>0</c> for
    /// custom roles, which therefore only ever satisfy an exact-name requirement.
    /// </summary>
    public static int RankOf(string role) => role?.Trim().ToLowerInvariant() switch
    {
        "viewer" or "reader" or "read" => 1,
        "operator" or "ops" => 2,
        "admin" or "administrator" => 3,
        _ => 0
    };

    /// <summary>
    /// True when <paramref name="held"/> satisfies <paramref name="required"/> — either by
    /// exact name (case-insensitive) or by outranking it on the well-known ladder.
    /// </summary>
    public static bool Satisfies(IReadOnlySet<string> held, IReadOnlyList<string> required)
    {
        foreach (var need in required)
        {
            if (held.Contains(need)) return true;

            var needRank = RankOf(need);
            if (needRank == 0) continue; // custom role — exact match only

            foreach (var have in held)
                if (RankOf(have) >= needRank) return true;
        }

        return false;
    }
}

/// <summary>
/// Declares the role(s) required to invoke a controller action. Applied to a method it
/// governs that action; applied to a controller it governs every action the controller
/// declares (a method-level attribute wins over the controller-level one).
/// <para>
/// Multiple roles are OR-ed: <c>[RequiresRole(TsakRoles.Operator, "release-bot")]</c> admits
/// a key holding either. Enforcement happens in <see cref="RoleAuthorizationProcessor"/>,
/// which runs only for authenticated requests — auth-exempt technical endpoints
/// (Kubernetes probes) are never gated.
/// </para>
/// <para>
/// Actions with no attribute fall back to a method-based default: <c>GET</c> and <c>HEAD</c>
/// require <see cref="TsakRoles.Viewer"/>, everything else requires
/// <see cref="TsakRoles.Operator"/>. A new endpoint is therefore never open by accident.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequiresRoleAttribute : Attribute
{
    /// <summary>Roles that satisfy this requirement (OR semantics).</summary>
    public string[] Roles { get; }

    public RequiresRoleAttribute(params string[] roles)
    {
        Roles = roles is { Length: > 0 }
            ? roles
            : throw new ArgumentException("At least one role must be specified.", nameof(roles));
    }
}

/// <summary>
/// Marks an action (or a whole controller) as requiring no particular role — a valid API key
/// is enough. Intended for <b>technical endpoints</b>: Kubernetes probes, echo-style
/// reachability checks, and anything a load balancer or orchestrator calls.
/// <para>
/// Those endpoints are normally auth-exempt altogether (<c>Tsak:Api:AuthExempt</c>), so the
/// role check never even reaches them. This attribute is the second line of defence: if an
/// operator narrows the exempt list, the endpoint starts requiring a key but still must not
/// start demanding privileges — a probe that answers <c>403</c> would take the pod out of
/// rotation for the wrong reason.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NoRoleRequiredAttribute : Attribute
{
}
