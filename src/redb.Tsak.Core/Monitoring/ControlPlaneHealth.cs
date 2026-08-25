using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Shared flag recording whether the <c>_system</c> control-plane context (REST/management API) started
/// successfully. If it failed, the host keeps running the business contexts, but the node is NOT fully
/// operational — this must be reflected in readiness rather than silently reported healthy
/// (review item 4.1).
/// </summary>
public sealed class ControlPlaneHealth
{
    private volatile bool _failed;
    private volatile string? _reason;

    /// <summary>True when the <c>_system</c> control-plane failed to start.</summary>
    public bool Failed => _failed;

    /// <summary>Human-readable reason for the failure (if any).</summary>
    public string? Reason => _reason;

    /// <summary>Marks the control-plane as failed to start (called once, by the host).</summary>
    public void MarkFailed(string reason)
    {
        _reason = reason;
        _failed = true;
    }
}

/// <summary>
/// Reports the control-plane as <see cref="HealthStatus.Unhealthy"/> when the <c>_system</c> context
/// failed to start, so the readiness probe stops reporting a control-plane-less node as ready.
/// </summary>
internal sealed class ControlPlaneHealthContributor : IHealthContributor
{
    private readonly ControlPlaneHealth _state;

    public ControlPlaneHealthContributor(ControlPlaneHealth state) => _state = state;

    public void Contribute(HealthEvaluation result)
    {
        result.Checks["control-plane"] = _state.Failed ? HealthStatus.Unhealthy : HealthStatus.Healthy;
    }
}
