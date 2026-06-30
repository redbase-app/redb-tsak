namespace redb.Tsak.Core.Contracts;

/// <summary>
/// Contributes additional health checks to <see cref="Monitoring.HealthCheckService"/>.
/// Registered via DI; each contributor adds entries to the evaluation result.
/// </summary>
public interface IHealthContributor
{
    /// <summary>Adds health check entries to the evaluation result.</summary>
    void Contribute(Monitoring.HealthEvaluation result);
}
