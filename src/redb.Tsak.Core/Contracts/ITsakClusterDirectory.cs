namespace redb.Tsak.Core.Contracts;

/// <summary>
/// How many Tsak clusters share the database. Implemented by redb.Tsak.Core.Pro from the cluster
/// roots and registered with cluster mode; absent otherwise. Core stores use it to decide whether a
/// record written before cluster isolation — it carries no cluster — can be attributed to this node's
/// cluster: only while the database holds a single one.
/// </summary>
public interface ITsakClusterDirectory
{
    /// <summary>Number of Tsak clusters (cluster roots) in the database.</summary>
    Task<int> CountClustersAsync(CancellationToken ct = default);
}
