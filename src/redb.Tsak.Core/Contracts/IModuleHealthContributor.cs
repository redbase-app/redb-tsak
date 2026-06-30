using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Per-module health probe SPI. Modules can register an implementation via DI to expose
/// their own health status into the aggregated <see cref="Monitoring.HealthCheckService"/>
/// result without coupling to the broader <see cref="IHealthContributor"/> interface
/// (which has access to the entire <see cref="Monitoring.HealthEvaluation"/> bag).
/// <para>
/// This interface is intentionally separate from <see cref="ITsakModule"/> so that adding a
/// health probe to an existing module is non-breaking — module authors implement this only
/// when they want to surface module-specific signals.
/// </para>
/// <para>
/// Each contributor's status is added to the evaluation under the key <c>"module:{ModuleName}"</c>.
/// Exceptions thrown from <see cref="CheckHealthAsync"/> are translated to
/// <see cref="HealthStatus.Unhealthy"/> so a misbehaving probe cannot crash the health endpoint.
/// </para>
/// </summary>
public interface IModuleHealthContributor
{
    /// <summary>
    /// Stable, short name used as the entry key in the health evaluation bag (e.g. <c>"orders"</c> →
    /// <c>"module:orders"</c>). Should be unique across registered contributors; ties are merged
    /// by taking the worst status.
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Returns the current health status of the module. Implementations should be fast (single-digit
    /// ms target) and must respect <paramref name="ct"/>.
    /// </summary>
    Task<HealthStatus> CheckHealthAsync(CancellationToken ct);
}
