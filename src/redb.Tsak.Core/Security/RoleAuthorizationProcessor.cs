using System.Collections.Concurrent;
using System.Reflection;
using redb.Route.Abstractions;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using Serilog;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Enforces <see cref="RequiresRoleAttribute"/> on controller actions.
/// Runs after <see cref="AuthorizeProcessor"/> and before
/// <see cref="ControllerDispatcherProcessor"/>: it resolves the action the request would
/// hit, works out the required role, and answers <c>403</c> when the caller's key does not
/// hold it.
/// <para>
/// <b>Technical endpoints are never gated.</b> The processor no-ops unless the exchange
/// carries <c>auth.authenticated = true</c>, so auth-exempt paths (Kubernetes probes under
/// <c>/api/health/*</c>) pass straight through, as do all requests when auth is disabled.
/// The echo and Prometheus routes have their own pipelines and never reach this processor
/// at all. On top of that, an action marked <see cref="NoRoleRequiredAttribute"/> is skipped
/// even when it *is* authenticated.
/// </para>
/// <para>
/// Actions without an explicit attribute fall back to a safe default derived from the HTTP
/// method: reads need <see cref="TsakRoles.Viewer"/>, writes need
/// <see cref="TsakRoles.Operator"/>.
/// </para>
/// </summary>
public sealed class RoleAuthorizationProcessor : IProcessor
{
    private static readonly ILogger Log = Serilog.Log.ForContext<RoleAuthorizationProcessor>();

    /// <summary>Sentinel stored in the cache for actions that must not be gated.</summary>
    private static readonly string[] NoGate = Array.Empty<string>();

    private readonly ControllerRegistry _registry;
    private readonly bool _rolelessKeysAreAdmin;

    /// <summary>Resolved requirement per action method — attribute lookup is reflection, cache it.</summary>
    private readonly ConcurrentDictionary<MethodInfo, string[]> _cache = new();

    /// <summary>Key ids already warned about, so a legacy key logs once, not once per request.</summary>
    private readonly ConcurrentDictionary<string, byte> _warnedKeys = new();

    /// <param name="registry">The same registry the dispatcher uses, so both resolve identically.</param>
    /// <param name="rolelessKeysAreAdmin">
    /// Back-compat switch (<c>Tsak:Auth:RolelessKeysAreAdmin</c>, default <c>false</c> = fail-closed):
    /// a key with no roles is DENIED role-gated endpoints. Set to <c>true</c> only to keep pre-RBAC keys
    /// (issued before roles were enforced, carrying no roles) working as <c>admin</c> during migration.
    /// </param>
    public RoleAuthorizationProcessor(ControllerRegistry registry, bool rolelessKeysAreAdmin = false)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rolelessKeysAreAdmin = rolelessKeysAreAdmin;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Anonymous request. Either auth is off, or this is an auth-exempt technical
        // endpoint (k8s probes). Nothing to authorize — never gate those.
        if (exchange.Properties.TryGetValue("auth.authenticated", out var authenticated) is false
            || authenticated is not true)
            return Task.CompletedTask;

        var path = exchange.In.Headers.TryGetValue(ControllerDispatcherProcessor.PathHeader, out var p)
            ? p?.ToString() : null;
        var method = exchange.In.Headers.TryGetValue(ControllerDispatcherProcessor.MethodHeader, out var m)
            ? m?.ToString() : null;

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(method))
            return Task.CompletedTask; // dispatcher will answer 400

        var action = _registry.Resolve(method, path, out _);
        if (action is null)
            return Task.CompletedTask; // dispatcher will answer 404 — do not leak existence via 403

        var required = RequiredRolesFor(action);
        if (ReferenceEquals(required, NoGate))
            return Task.CompletedTask;

        var held = ParseRoles(exchange.Properties.TryGetValue("auth.roles", out var r) ? r?.ToString() : null);

        if (held.Count == 0)
        {
            var keyId = exchange.Properties.TryGetValue("auth.key-id", out var k) ? k?.ToString() ?? "?" : "?";
            if (_rolelessKeysAreAdmin)
            {
                if (_warnedKeys.TryAdd(keyId, 0))
                    Log.Warning(
                        "API key {KeyId} carries no roles — treated as '{Admin}' for compatibility. " +
                        "Re-issue it with explicit roles, or set Tsak:Auth:RolelessKeysAreAdmin=false to deny.",
                        keyId, TsakRoles.Admin);
                return Task.CompletedTask;
            }

            Log.Warning("API request rejected: key {KeyId} carries no roles, required {Required}",
                keyId, string.Join(" or ", required));
            Deny(exchange, required);
            return Task.CompletedTask;
        }

        if (TsakRoles.Satisfies(held, required))
            return Task.CompletedTask;

        Log.Warning(
            "API request rejected: insufficient role for {Method} {Path}. Required: {Required}, has: {Held}",
            method, path, string.Join(" or ", required), string.Join(",", held));
        Deny(exchange, required);
        return Task.CompletedTask;
    }

    private static void Deny(IExchange exchange, IReadOnlyList<string> required)
    {
        ApiResponse.Forbidden(exchange, $"Insufficient permissions. Required role: {string.Join(" or ", required)}.");
        exchange.Stop();
    }

    /// <summary>
    /// Requirement for an action: method attribute → controller attribute → default by HTTP method.
    /// Returns <see cref="NoGate"/> when the action opts out via <see cref="NoRoleRequiredAttribute"/>.
    /// </summary>
    private string[] RequiredRolesFor(ControllerAction action) =>
        _cache.GetOrAdd(action.Method, _ =>
        {
            if (action.Method.GetCustomAttribute<NoRoleRequiredAttribute>(inherit: true) is not null
                || action.ControllerType.GetCustomAttribute<NoRoleRequiredAttribute>(inherit: true) is not null)
                return NoGate;

            var attr = action.Method.GetCustomAttribute<RequiresRoleAttribute>(inherit: true)
                       ?? action.ControllerType.GetCustomAttribute<RequiresRoleAttribute>(inherit: true);

            if (attr is not null)
                return attr.Roles;

            // No attribute — safe default so a new endpoint is never open by accident.
            return action.HttpMethod is HttpMethodType.Get
                ? new[] { TsakRoles.Viewer }
                : new[] { TsakRoles.Operator };
        });

    private static IReadOnlySet<string> ParseRoles(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
}
