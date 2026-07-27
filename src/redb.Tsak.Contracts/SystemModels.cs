using System.Text.Json.Serialization;

namespace redb.Tsak.Contracts;

/// <summary>
/// Effective (merged, resolved) configuration for this node, from <c>GET /api/system/config</c>.
/// Flat key → value map over the <c>Tsak:*</c> and <c>ConnectionStrings:*</c> sections, with secrets
/// redacted. Answers "what settings is this node actually running with" on an incident.
/// </summary>
public sealed record EffectiveConfigResult
{
    public required bool Available { get; init; }
    public int RedactedCount { get; init; }
    public IReadOnlyDictionary<string, string?> Values { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>
/// Health check states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// Result of a health check evaluation.
/// </summary>
public sealed record HealthCheckResult
{
    public HealthStatus Status { get; init; }
    public string Description { get; init; } = "";
    public Dictionary<string, HealthStatus> Checks { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// System metrics response.
/// </summary>
public sealed record MetricsResponse
{
    public required bool Available { get; init; }
    public int? StoredPoints { get; init; }
    public MetricSnapshot? Latest { get; init; }
}

/// <summary>
/// Metrics history response (array of snapshots over a time window).
/// </summary>
public sealed record MetricsHistoryResponse
{
    public required bool Available { get; init; }
    public MetricSnapshot[] Points { get; init; } = [];
}

/// <summary>
/// A single snapshot of process and system metrics at a point in time.
/// </summary>
public sealed record MetricSnapshot
{
    public DateTime Timestamp { get; init; }
    public long TimestampMs { get; init; }
    public CpuMetrics Cpu { get; init; } = new();
    public MemoryMetrics Memory { get; init; } = new();
    public ThreadingMetrics Threading { get; init; } = new();
    public GcMetrics GarbageCollector { get; init; } = new();
}

public sealed record CpuMetrics
{
    public double ProcessUsage { get; init; }
    public double SystemUsage { get; init; }
}

public sealed record MemoryMetrics
{
    public double WorkingSetMB { get; init; }
    public double PrivateMemoryMB { get; init; }
    public double VirtualMemoryMB { get; init; }
    public double GCMemoryMB { get; init; }
    public double SystemMemoryUsageMB { get; init; }
}

public sealed record ThreadingMetrics
{
    public int ActiveThreads { get; init; }
    public long ThreadPoolPendingWork { get; init; }
}

public sealed record GcMetrics
{
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double TotalMemoryMB { get; init; }
}

/// <summary>
/// System information response.
/// </summary>
public sealed record SystemInfoResponse
{
    public required string Version { get; init; }
    public required DateTime StartedAt { get; init; }
    public required string Uptime { get; init; }
    public required int ContextCount { get; init; }
    public required int ModuleCount { get; init; }
    public required string MachineName { get; init; }
    public required int ProcessorCount { get; init; }
    public required double WorkingSetMb { get; init; }
}

/// <summary>
/// Standard API error response.
/// </summary>
public sealed record ApiErrorResponse
{
    public required string Error { get; init; }
    public required string Message { get; init; }
    public required int StatusCode { get; init; }
}
