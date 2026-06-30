using System.Reflection;
using redb.Route.Abstractions;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Plugin for enriching the _system REST API context with additional controllers and services.
/// Registered via DI by Pro modules; called by <see cref="Services.SystemContextBuilder"/>.
/// </summary>
public interface ISystemContextPlugin
{
    /// <summary>Register additional services into the system context.</summary>
    void ConfigureServices(IRouteContext context, IServiceProvider rootServiceProvider);

    /// <summary>Return assemblies containing additional controllers to register.</summary>
    IEnumerable<Assembly> GetControllerAssemblies();
}
