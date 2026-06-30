using redb.Route.Abstractions;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Manages lifecycle of route contexts: create, start, stop, remove.
/// Supports named (config-driven) and anonymous (auto-created) contexts.
/// Equivalent of lt.tsak IRouteContextManager.
/// </summary>
public interface ITsakContextManager
{
    /// <summary>
    /// Creates a named context with the given configuration.
    /// </summary>
    /// <param name="contextName">Unique context name.</param>
    /// <param name="serviceProvider">DI service provider for the context.</param>
    /// <param name="configuration">Context-specific configuration values.</param>
    /// <returns>Created route context.</returns>
    IRouteContext CreateContext(string contextName, IServiceProvider serviceProvider, IDictionary<string, object?> configuration);

    /// <summary>Gets a context by name.</summary>
    IRouteContext? GetContext(string contextName);

    /// <summary>Returns all contexts as (name, context) pairs.</summary>
    IReadOnlyList<(string Name, IRouteContext Context)> GetAllContexts();

    /// <summary>Starts a context. When ignoreAutoStart is true, skips the autoStart check (for manual API/CLI calls).</summary>
    Task StartContextAsync(string contextName, bool ignoreAutoStart = false);

    /// <summary>Stops a context with graceful drain. Returns drain diagnostics.</summary>
    /// <param name="contextName">Context name.</param>
    /// <param name="setAutoStartFalse">If true, marks context to not auto-start on next boot.</param>
    /// <param name="ct">Cancellation token for graceful shutdown timeout.</param>
    Task<ContextActionResponse> StopContextAsync(string contextName, bool setAutoStartFalse = true, CancellationToken ct = default);

    /// <summary>Restarts a context (drain-aware stop + start). Returns drain diagnostics.</summary>
    Task<ContextActionResponse> RestartContextAsync(string contextName, CancellationToken ct = default);

    /// <summary>Removes a context (stop + dispose + unregister).</summary>
    /// <param name="contextName">Context name.</param>
    /// <param name="preserveState">When true, preserves autoStart state in store (for hot-reload). When false, cleans up everything.</param>
    Task RemoveContextAsync(string contextName, bool preserveState = false);

    /// <summary>Returns whether the context should auto-start based on config.</summary>
    bool ShouldAutoStart(string contextName);

    /// <summary>Async version of <see cref="ShouldAutoStart"/> — avoids sync-over-async for Redb-backed store.</summary>
    Task<bool> ShouldAutoStartAsync(string contextName);
}
