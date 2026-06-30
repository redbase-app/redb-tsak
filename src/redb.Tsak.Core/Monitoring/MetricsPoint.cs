namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// A single snapshot of process and system metrics at a point in time.
/// </summary>
public class MetricsPoint
{
    public DateTime Timestamp { get; set; }
    public long TimestampMs { get; set; }
    public CpuMetrics Cpu { get; set; } = new();
    public MemoryMetrics Memory { get; set; } = new();
    public ThreadingMetrics Threading { get; set; } = new();
    public GcMetrics GarbageCollector { get; set; } = new();
}

public class CpuMetrics
{
    public double ProcessUsage { get; set; }
    public double SystemUsage { get; set; }
}

public class MemoryMetrics
{
    public double WorkingSetMB { get; set; }
    public double PrivateMemoryMB { get; set; }
    public double VirtualMemoryMB { get; set; }
    public double GCMemoryMB { get; set; }
    public double SystemMemoryUsageMB { get; set; }
}

public class ThreadingMetrics
{
    public int ActiveThreads { get; set; }
    public long ThreadPoolPendingWork { get; set; }
}

public class GcMetrics
{
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public double TotalMemoryMB { get; set; }
}
