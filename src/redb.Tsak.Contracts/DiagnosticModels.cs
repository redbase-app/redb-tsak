namespace redb.Tsak.Contracts;

/// <summary>
/// Full system diagnostic dump — analogous to a JVM thread dump,
/// adapted for .NET (no per-thread stacks).
/// </summary>
public sealed record DiagnosticDump
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<InflightExchangeDto> InflightExchanges { get; init; } = [];
    public required ThreadPoolDiagnostics ThreadPool { get; init; }
    public required GcDiagnostics GarbageCollector { get; init; }
    public IReadOnlyList<DiagnosticContextSummary> Contexts { get; init; } = [];
    public IReadOnlyList<WatchdogAlert> ActiveAlerts { get; init; } = [];
    public IReadOnlyList<LifecycleEvent> RecentEvents { get; init; } = [];
    public SystemMetricsSnapshot? SystemMetrics { get; init; }
}

/// <summary>
/// Per-context summary in the diagnostic dump.
/// </summary>
public sealed record DiagnosticContextSummary
{
    public required string ContextName { get; init; }
    public bool IsStarted { get; init; }
    public int RouteCount { get; init; }
    public int EndpointCount { get; init; }
    public int InflightCount { get; init; }
}

/// <summary>
/// Thread pool snapshot for diagnostics.
/// </summary>
public sealed record ThreadPoolDiagnostics
{
    public long PendingWorkItems { get; init; }
    public int WorkerThreadsAvailable { get; init; }
    public int WorkerThreadsMin { get; init; }
    public int WorkerThreadsMax { get; init; }
    public int IoThreadsAvailable { get; init; }
    public int IoThreadsMin { get; init; }
    public int IoThreadsMax { get; init; }
}

/// <summary>
/// GC statistics snapshot for diagnostics.
/// </summary>
public sealed record GcDiagnostics
{
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double TotalMemoryMB { get; init; }
}

/// <summary>
/// Diagnostic dump for a single route.
/// </summary>
public sealed record RouteDiagnosticDump
{
    public required string RouteId { get; init; }
    public required string FromUri { get; init; }
    public required string Status { get; init; }
    public bool AutoStart { get; init; }
    public IReadOnlyList<InflightExchangeDto> InflightExchanges { get; init; } = [];
    public RouteMetricsSummary? Metrics { get; init; }
    public IReadOnlyList<string> RecentErrors { get; init; } = [];
    public IReadOnlyList<LifecycleEvent> LifecycleHistory { get; init; } = [];
}
