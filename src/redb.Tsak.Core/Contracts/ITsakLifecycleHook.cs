namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Generic lifecycle hook for Tsak startup/shutdown.
/// Registered via DI, called by TsakHostedService in order.
/// </summary>
public interface ITsakLifecycleHook
{
    /// <summary>Execution order (lower = earlier).</summary>
    int Order => 0;

    /// <summary>Called during Tsak startup, before module discovery.</summary>
    Task OnStartingAsync(CancellationToken ct);

    /// <summary>Called during Tsak shutdown, after all contexts stopped.</summary>
    Task OnStoppingAsync(CancellationToken ct);
}
