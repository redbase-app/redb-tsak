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

    /// <summary>
    /// Requests shed by an admission limit (e.g. <c>maxConcurrentRequests</c>) BEFORE a pipeline
    /// ran: the transport answered 429/503 and no exchange was created, so these are counted in
    /// neither <see cref="MessagesProcessed"/> nor <see cref="Errors"/>. A growing value with zero
    /// errors means the service is healthy but under-provisioned (or facing a surge).
    /// </summary>
    public long Rejected { get; init; }

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

    /// <summary>
    /// Routes currently shedding load: <see cref="RouteMetricsSummary.Rejected"/> &gt; 0.
    /// Deliberately separate from <see cref="ErrorProneRoutes"/> — a route sitting at its
    /// concurrency ceiling with zero errors is healthy but under-provisioned, and the operator
    /// response (raise the limit, add nodes) differs from bug-hunting.
    /// </summary>
    public IReadOnlyList<RouteMetricsSummary> SheddingRoutes { get; init; } = [];
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
