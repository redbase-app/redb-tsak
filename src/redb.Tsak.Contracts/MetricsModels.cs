namespace redb.Tsak.Contracts;

/// <summary>
/// Per-route metrics summary, derived from the route's consumer endpoint statistics.
/// </summary>
public sealed record RouteMetricsSummary
{
    public required string RouteId { get; init; }
    public required string Status { get; init; }
    public int InflightCount { get; init; }
    public long MessagesProcessed { get; init; }
    public long Errors { get; init; }
    public long Warnings { get; init; }
    public double AvgProcessingMs { get; init; }
    public double ThroughputPerSecond { get; init; }
    public DateTime? LastActivity { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorTime { get; init; }
    public string? LastWarning { get; init; }
    public DateTime? LastWarningTime { get; init; }
    public string? Uptime { get; init; }
}

/// <summary>
/// Aggregated dashboard snapshot for the web UI.
/// </summary>
public sealed record DashboardSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int TotalContexts { get; init; }
    public int RunningContexts { get; init; }
    public int TotalInflight { get; init; }
    public IReadOnlyList<RouteMetricsSummary> BusiestRoutes { get; init; } = [];
    public IReadOnlyList<RouteMetricsSummary> ErrorProneRoutes { get; init; } = [];
    public IReadOnlyList<WatchdogAlert> ActiveAlerts { get; init; } = [];
    public SystemMetricsSnapshot? SystemMetrics { get; init; }
}

/// <summary>
/// Lightweight system metrics for the dashboard.
/// </summary>
public sealed record SystemMetricsSnapshot
{
    public double CpuPercent { get; init; }
    public double MemoryMB { get; init; }
    public int ThreadCount { get; init; }
    public int Gen2Collections { get; init; }
}
