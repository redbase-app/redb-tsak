using System.Reflection;
using redb.Route.Abstractions;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Modules;

/// <summary>
/// Adapter wrapping a static <c>InitRoute.main</c> method as an ITsakModule, in either form of the convention
/// (<see cref="InitRouteConvention"/>): <c>IRouteContext main(IRouteContext)</c> runs through
/// <see cref="Initialize"/>, <c>Task&lt;IRouteContext&gt; main(IRouteContext)</c> through
/// <see cref="InitializeAsync"/>. An exception from <c>main</c> reaches the caller as the module threw it, not
/// wrapped in <see cref="TargetInvocationException"/>, so the log and the activation report name the module's
/// failure. Module name is derived from the namespace of the InitRoute class.
/// </summary>
public class StaticMethodModule : ITsakModule, IEmbeddedConfigModule
{
    private readonly MethodInfo _mainMethod;
    private readonly bool _isAsync;

    public StaticMethodModule(string moduleName, string version, MethodInfo mainMethod, string? sourceDirectory = null, string? embeddedConfigJson = null)
    {
        ModuleName = moduleName;
        Version = version;
        _mainMethod = mainMethod;
        _isAsync = InitRouteConvention.Classify(mainMethod) == InitRouteConvention.Form.Async;
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

    /// <summary>Runs a synchronous <c>main</c>. An async <c>main</c> is refused: it runs only through <see cref="InitializeAsync"/>.</summary>
    public IRouteContext Initialize(IRouteContext context)
    {
        if (_isAsync)
            throw new InvalidOperationException(
                $"{_mainMethod.DeclaringType?.FullName}.main is async (Task<IRouteContext>) and cannot run synchronously; "
                + "initialize the module through InitializeAsync.");

        var result = Invoke(context);
        Status = TsakModuleStatus.Initialized;
        return result as IRouteContext ?? context;
    }

    /// <summary>Awaits an async <c>main</c>; a synchronous <c>main</c> runs as in <see cref="Initialize"/>.</summary>
    public async Task<IRouteContext> InitializeAsync(IRouteContext context)
    {
        if (!_isAsync)
            return Initialize(context);

        var task = Invoke(context) as Task
                   ?? throw new InvalidOperationException(
                       $"{_mainMethod.DeclaringType?.FullName}.main returned no Task.");
        await task.ConfigureAwait(false);

        // The result is read through reflection: the declared form may be Task<RouteContext>, which is not a
        // Task<IRouteContext>.
        var result = task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
        Status = TsakModuleStatus.Initialized;
        return result as IRouteContext ?? context;
    }

    private object? Invoke(IRouteContext context) =>
        _mainMethod.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, parameters: [context], culture: null);
}
