using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using redb.Route.Controllers;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Extensions;

/// <summary>
/// DI helpers for registering Tsak admin audit components.
/// </summary>
public static class AdminAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default admin audit pipeline:
    /// <list type="bullet">
    ///   <item><see cref="LogAdminAuditService"/> as <see cref="IAdminAuditService"/></item>
    ///   <item><see cref="AdminAuditFilter"/> as <see cref="IControllerActionFilter"/></item>
    /// </list>
    /// Both registrations use <c>TryAddEnumerable</c>/<c>TryAdd</c>, so calling this multiple
    /// times is safe and downstream replacements (e.g. Pro's redb-backed sink) win.
    /// </summary>
    public static IServiceCollection AddTsakAdminAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAdminAuditService, LogAdminAuditService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IControllerActionFilter, AdminAuditFilter>());

        return services;
    }
}
