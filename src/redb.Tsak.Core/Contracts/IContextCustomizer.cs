using redb.Route.Abstractions;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Customizes a newly created RouteContext (add policies, services, etc.).
/// Registered via DI; called by TsakContextManager after context creation.
/// </summary>
public interface IContextCustomizer
{
    /// <summary>Called when a new context is being initialized.</summary>
    Task CustomizeAsync(IRouteContext context, IServiceProvider serviceProvider, string contextName);
}
