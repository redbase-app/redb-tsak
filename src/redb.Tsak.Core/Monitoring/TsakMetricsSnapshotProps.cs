using redb.Core.Attributes;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// EAV model for storing per-node metrics snapshots in redb.
/// Each node writes its snapshot periodically (ClusterReportIntervalSeconds).
/// Used for cluster-wide metrics aggregation.
/// </summary>
[RedbScheme("_tsak_metrics")]
public class TsakMetricsSnapshotProps
{
    public string NodeId { get; set; } = string.Empty;
    public DateTimeOffset CollectedAt { get; set; }

    // CPU
    public double CpuProcess { get; set; }
    public double CpuSystem { get; set; }

    // Memory (MB)
    public double MemoryWorkingSet { get; set; }
    public double MemoryGC { get; set; }

    // Threading
    public int ActiveThreads { get; set; }

    // GC
    public int GcGen0 { get; set; }
    public int GcGen1 { get; set; }
    public int GcGen2 { get; set; }

    // Context summary
    public int ContextCount { get; set; }
    public int ContextsRunning { get; set; }
}
