using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Adapter wrapping an IRouteBuilder implementation as an ITsakModule.
/// During Initialize a <see cref="RouteBuilder"/> is registered with the context via
/// <c>AddRoutes</c> so its routes are actually compiled at Start (issue #3).
/// </summary>
public class RouteBuilderModule : ITsakModule
{
    private readonly IRouteBuilder _builder;

    public RouteBuilderModule(string moduleName, string version, IRouteBuilder builder, string? sourceDirectory = null)
    {
        ModuleName = moduleName;
        Version = version;
        _builder = builder;
        SourceDirectory = sourceDirectory;
    }

    public string ModuleName { get; }
    public string Version { get; }
    public string Description => $"RouteBuilder module: {_builder.GetType().FullName}";
    public IReadOnlyList<string> Dependencies => [];
    public bool CanInitialize => true;
    public TsakModuleStatus Status { get; internal set; } = TsakModuleStatus.Loaded;
    public string? SourceDirectory { get; }

    public IRouteContext Initialize(IRouteContext context)
    {
        // The context compiles ONLY builders registered via AddRoutes (it appends them to its own list
        // and runs their build at Start). Calling _builder.Configure(context) directly just fills the
        // builder's OWN definition list, which the context never reads — the routes silently never
        // compile ("started successfully: all 0 endpoints operational", issue #3). Register instead.
        if (_builder is RouteBuilder routeBuilder)
        {
            if (context is not RouteContext routeContext)
                throw new InvalidOperationException(
                    $"RouteBuilderModule '{ModuleName}' needs a RouteContext to register its routes, but got " +
                    $"{context?.GetType().FullName ?? "null"}. A RouteBuilder's routes are compiled only when the " +
                    "builder is added to the context via AddRoutes.");

            routeContext.AddRoutes(routeBuilder);
        }
        else
        {
            // A hand-rolled IRouteBuilder that is not a RouteBuilder is expected to register its own
            // routes on the context inside Configure — call it directly (best-effort).
            _builder.Configure(context);
        }

        Status = TsakModuleStatus.Initialized;
        return context;
    }
}
