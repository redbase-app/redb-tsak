using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Modules;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Reports the node as <see cref="HealthStatus.Degraded"/> while more module load contexts stay in memory after Tsak let
/// them go than <see cref="Services.HotReloadOptions.MaxOrphanedContexts"/> allows: the node keeps working, but only a
/// restart gives that memory back. Reads the count the runtime holds, no collection runs here.
/// </summary>
internal sealed class HotReloadHealthContributor(ModuleContextMonitor monitor) : IHealthContributor
{
    /// <summary>The key of this check in <see cref="HealthEvaluation.Checks"/>.</summary>
    internal const string CheckName = "hotreload";

    public void Contribute(HealthEvaluation result)
    {
        if (monitor.Notice is { } notice)
        {
            result.Checks[CheckName] = HealthStatus.Degraded;
            result.Details[CheckName] = notice;
            return;
        }
        result.Checks[CheckName] = HealthStatus.Healthy;
    }
}
