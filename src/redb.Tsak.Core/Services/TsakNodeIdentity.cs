using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Services;

/// <summary>
/// The identity of this worker node: <c>Tsak:Cluster:NodeId</c>, or the host name when it is not set. One rule for the
/// Tsak cluster (its node record, locks, leader election) and for the Quartz schedulers of the contexts, whose
/// <c>quartz.scheduler.instanceId = NODE</c> stands for this value.
/// </summary>
public static class TsakNodeIdentity
{
    /// <summary>The value of <c>quartz.scheduler.instanceId</c> that Tsak replaces with the node id.</summary>
    public const string QuartzInstanceIdOfNode = "NODE";

    /// <summary>The node id from a configured value: the value when set, the host name otherwise.</summary>
    public static string Resolve(string? configuredNodeId)
        => string.IsNullOrWhiteSpace(configuredNodeId) ? Environment.MachineName : configuredNodeId.Trim();

    /// <summary>The node id of <paramref name="configuration"/> (<c>Tsak:Cluster:NodeId</c>).</summary>
    public static string Resolve(IConfiguration configuration) => Resolve(configuration["Tsak:Cluster:NodeId"]);
}
