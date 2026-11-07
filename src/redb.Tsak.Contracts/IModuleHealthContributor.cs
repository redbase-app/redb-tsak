namespace redb.Tsak.Contracts;

/// <summary>
/// Per-module health probe SPI. Modules register an implementation via DI to surface their own
/// status into the aggregated <c>/api/health/{startup,live,ready}</c> endpoints, without coupling
/// to the host-internal <c>IHealthContributor</c> (which sees the whole evaluation bag).
/// <para>
/// Lives in <c>redb.Tsak.Contracts</c> — the lightweight contract assembly — precisely so a
/// module (e.g. redb.Identity) can implement it without taking a compile-time dependency on the
/// full <c>redb.Tsak.Core</c> host and its transitive graph. <see cref="HealthStatus"/> is the
/// only type it touches, and it lives here too.
/// </para>
/// <para>
/// Each contributor's status is added to the evaluation under the key <c>"module:{ModuleName}"</c>.
/// Exceptions thrown from <see cref="CheckHealthAsync"/> are translated to
/// <see cref="HealthStatus.Unhealthy"/> by the host so a misbehaving probe cannot crash the
/// health endpoint.
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
