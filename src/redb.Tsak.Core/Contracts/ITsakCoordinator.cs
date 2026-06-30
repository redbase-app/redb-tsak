namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Orchestrates module-to-context mapping.
/// Subscribes to registry events and creates/updates/removes contexts accordingly.
/// Equivalent of lt.tsak ModuleContextCoordinator.
/// </summary>
public interface ITsakCoordinator
{
    /// <summary>
    /// Initializes the coordinator and subscribes to registry events.
    /// Must be called before any modules are registered.
    /// </summary>
    /// <param name="registry">Module registry to subscribe to.</param>
    void Initialize(ITsakModuleRegistry registry);

    /// <summary>
    /// Handles a batch of newly added modules (e.g. from static providers).
    /// Creates contexts and initializes modules according to the module mapping config.
    /// </summary>
    /// <param name="modules">Modules to process.</param>
    Task ProcessBatchAsync(IReadOnlyList<ITsakModule> modules);

    /// <summary>
    /// Handles a single newly added module.
    /// </summary>
    /// <param name="module">Module to process.</param>
    Task ProcessModuleAddedAsync(ITsakModule module);

    /// <summary>
    /// Handles module removal — stops and cleans up the associated context.
    /// </summary>
    /// <param name="moduleName">Name of the removed module.</param>
    Task ProcessModuleRemovedAsync(string moduleName);

    /// <summary>
    /// Handles module update — restarts the associated context with the new version.
    /// </summary>
    /// <param name="module">Updated module.</param>
    Task ProcessModuleUpdatedAsync(ITsakModule module);
}
