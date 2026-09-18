using Microsoft.Extensions.Configuration;

namespace redb.Tsak.Core.Services.Storage;

/// <summary>
/// Cluster scope of the redb-backed Tsak stores. Several Tsak clusters may share one database, so a
/// stored key carries the cluster name from <c>Tsak:Cluster:ClusterName</c>. The default matches the
/// cluster options of redb.Tsak.Core.Pro, and a host without cluster mode uses it as well.
/// </summary>
internal static class TsakStorageScope
{
    public const string DefaultClusterName = "default";

    public static string ClusterName(IConfiguration? configuration) =>
        configuration?["Tsak:Cluster:ClusterName"] is { } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : DefaultClusterName;
}
