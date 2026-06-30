using redb.Route.Abstractions;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Adapter wrapping an IRouteBuilder implementation as an ITsakModule.
/// The builder's Configure(context) method is called during Initialize.
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
        _builder.Configure(context);
        Status = TsakModuleStatus.Initialized;
        return context;
    }
}
