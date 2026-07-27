using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Route.Controllers;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// DI helpers for registering Tsak admin audit components.
/// </summary>
public static class AdminAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the admin audit pipeline:
    /// <list type="bullet">
    ///   <item><see cref="LogAdminAuditService"/> — the always-present fallback sink (structured
    ///     WRN log line, JSON after a <c>[tsak-audit]</c> anchor).</item>
    ///   <item><see cref="RouteAdminAuditService"/> — the effective <see cref="IAdminAuditService"/>,
    ///     which persists to <c>tsak_audit_log</c> via <c>direct://tsak-audit</c> once
    ///     <c>SystemContextBuilder</c> attaches it, and delegates to the log sink until then and
    ///     whenever no database is configured.</item>
    ///   <item><see cref="AdminAuditFilter"/> as <see cref="IControllerActionFilter"/>.</item>
    ///   <item><see cref="AuditSchemaInitializer"/> — creates the table on startup per provider.</item>
    ///   <item><see cref="AuditQueryService"/> — backs <c>GET /api/audit</c> and the retention job.</item>
    /// </list>
    /// All registrations are idempotent, so calling this multiple times is safe.
    /// </summary>
    public static IServiceCollection AddTsakAdminAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The concrete log sink is always available for RouteAdminAuditService to fall back to.
        services.TryAddSingleton<LogAdminAuditService>();

        // Effective sink: route-backed, fire-and-forget, with the log sink as fallback.
        services.TryAddSingleton<IAdminAuditService>(sp => new RouteAdminAuditService(
            sp.GetRequiredService<LogAdminAuditService>(),
            sp.GetRequiredService<ILogger<RouteAdminAuditService>>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IControllerActionFilter, AdminAuditFilter>());

        // Schema creation + query service. The initializer no-ops without a provider.
        services.AddSingleton<IHostedService, AuditSchemaInitializer>();
        services.TryAddSingleton<AuditQueryService>();

        // Retention runs as a cron:// route mounted on the _system context (SystemContextBuilder),
        // not a bare Quartz job — nothing to register here.

        return services;
    }
}
