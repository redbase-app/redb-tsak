using System.Reflection;
using redb.Route.Abstractions;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Adapter wrapping a static InitRoute.main(IRouteContext) method as an ITsakModule.
/// Module name is derived from the namespace of the InitRoute class.
/// </summary>
public class StaticMethodModule : ITsakModule
{
    private readonly MethodInfo _mainMethod;

    public StaticMethodModule(string moduleName, string version, MethodInfo mainMethod, string? sourceDirectory = null, string? embeddedConfigJson = null)
    {
        ModuleName = moduleName;
        Version = version;
        _mainMethod = mainMethod;
        SourceDirectory = sourceDirectory;
        EmbeddedConfigJson = embeddedConfigJson;
    }

    public string ModuleName { get; }
    public string Version { get; }
    public string Description => $"Static method module from {_mainMethod.DeclaringType?.FullName}";
    public IReadOnlyList<string> Dependencies => [];
    public bool CanInitialize => true;
    public TsakModuleStatus Status { get; internal set; } = TsakModuleStatus.Loaded;
    public string? SourceDirectory { get; }

    /// <summary>
    /// Raw JSON config from .tpkg package ({moduleName}.config.json), loaded in-memory.
    /// Null for bare DLL modules (they use config file from disk).
    /// </summary>
    public string? EmbeddedConfigJson { get; internal set; }

    public IRouteContext Initialize(IRouteContext context)
    {
        var result = _mainMethod.Invoke(null, [context]);
        Status = TsakModuleStatus.Initialized;
        return result as IRouteContext ?? context;
    }
}
