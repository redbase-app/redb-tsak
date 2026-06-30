namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Registry managing discovery, registration, and lifecycle of route modules.
/// Thread-safe. Fires events for coordinator subscription.
/// Equivalent of lt.tsak IRouteModuleRegistry.
/// </summary>
public interface ITsakModuleRegistry
{
    /// <summary>
    /// Registers modules from static (compile-time) providers.
    /// Fires <see cref="ModulesBatchAdded"/> once with all registered modules.
    /// </summary>
    /// <param name="providers">Static module providers.</param>
    /// <returns>Number of modules registered.</returns>
    Task<int> RegisterStaticProvidersAsync(IEnumerable<IStaticModuleProvider> providers);

    /// <summary>
    /// Discovers modules in assemblies at the specified paths.
    /// Scans for ITsakModule implementations, InitRoute.main() conventions, and IRouteBuilder subclasses.
    /// </summary>
    /// <param name="assemblyPaths">Directory paths to scan for DLL files.</param>
    /// <returns>Number of modules discovered.</returns>
    Task<int> DiscoverModulesAsync(IEnumerable<string> assemblyPaths);

    /// <summary>Registers a single module manually.</summary>
    void RegisterModule(ITsakModule module);

    /// <summary>Unregisters a module by name.</summary>
    /// <returns>True if module was found and removed.</returns>
    bool UnregisterModule(string moduleName);

    /// <summary>Unregisters a module without firing events (used during package reload to preserve state).</summary>
    bool UnregisterModuleSilent(string moduleName);

    /// <summary>Replaces a module in the registry without firing events (used by hot-swap).</summary>
    void ReplaceModuleSilent(ITsakModule module);

    /// <summary>Returns all registered modules.</summary>
    IReadOnlyList<ITsakModule> GetAllModules();

    /// <summary>Gets a module by name.</summary>
    ITsakModule? GetModule(string moduleName);

    /// <summary>Returns modules with the specified status.</summary>
    IReadOnlyList<ITsakModule> GetModulesByStatus(TsakModuleStatus status);

    /// <summary>Fired when a single module is added.</summary>
    event EventHandler<ITsakModule> ModuleAdded;

    /// <summary>Fired when a single module is removed.</summary>
    event EventHandler<string> ModuleRemoved;

    /// <summary>Fired when a module is updated (new version detected).</summary>
    event EventHandler<ITsakModule> ModuleUpdated;

    /// <summary>Fired after a batch of modules is added (e.g. from static providers).</summary>
    event EventHandler<IReadOnlyList<ITsakModule>> ModulesBatchAdded;
}
