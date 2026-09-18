using redb.Route.Abstractions;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Status of a route module in its lifecycle.
/// </summary>
public enum TsakModuleStatus
{
    /// <summary>Module discovered but not yet loaded.</summary>
    Discovered,

    /// <summary>Module loaded into memory.</summary>
    Loaded,

    /// <summary>Module initialized (routes configured).</summary>
    Initialized,

    /// <summary>Module running (context started).</summary>
    Running,

    /// <summary>Module stopped.</summary>
    Stopped,

    /// <summary>Module failed to load or initialize.</summary>
    Faulted
}

/// <summary>
/// Contract for a route module that can be discovered, loaded, and initialized in a context.
/// Equivalent of lt.tsak IRouteModule, adapted for redb.Route.
/// </summary>
public interface ITsakModule
{
    /// <summary>Unique module name (e.g. "DAL.GPS", "Api.Mobile").</summary>
    string ModuleName { get; }

    /// <summary>Module version string.</summary>
    string Version { get; }

    /// <summary>Human-readable description.</summary>
    string Description { get; }

    /// <summary>Names of modules this module depends on.</summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>Whether the module can be initialized (all deps met, no errors).</summary>
    bool CanInitialize { get; }

    /// <summary>Current lifecycle status.</summary>
    TsakModuleStatus Status { get; }

    /// <summary>
    /// Directory where the module's DLL was loaded from.
    /// Used to locate module config files (context.json, {Module}.config.json).
    /// Null for static/in-process modules.
    /// </summary>
    string? SourceDirectory { get; }

    /// <summary>
    /// Initializes routes in the given context, synchronously. The coordinator reaches it through the
    /// default <see cref="InitializeAsync"/>.
    /// </summary>
    /// <param name="context">Route context to configure.</param>
    /// <returns>The configured context.</returns>
    IRouteContext Initialize(IRouteContext context);

    /// <summary>
    /// Initializes routes in the given context. This is the entry the coordinator awaits, and the context
    /// starts only after it completes. A module whose setup awaits (schema sync, reference data) overrides
    /// it; the default runs <see cref="Initialize"/>, so existing modules work unchanged, without a rebuild.
    /// <para>
    /// Interface mapping is fixed by the class that declares <see cref="ITsakModule"/>. A module that inherits
    /// the interface from a base class and adds <c>InitializeAsync</c> must list <see cref="ITsakModule"/>
    /// again (or the base class must declare a virtual <c>InitializeAsync</c> to override); otherwise this
    /// default runs and its <see cref="Initialize"/> is called instead.
    /// </para>
    /// </summary>
    /// <param name="context">Route context to configure.</param>
    /// <returns>The configured context.</returns>
    Task<IRouteContext> InitializeAsync(IRouteContext context) => Task.FromResult(Initialize(context));
}
