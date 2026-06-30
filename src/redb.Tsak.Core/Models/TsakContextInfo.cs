namespace redb.Tsak.Core.Models;

/// <summary>
/// Context snapshot DTO used by ContextInfoCollector.
/// Also serialized into <c>TsakNodeProps.Contexts</c> for cluster heartbeat propagation.
/// </summary>
public class TsakContextInfo
{
    public string ContextName { get; set; } = string.Empty;
    public string Status { get; set; } = "Stopped";
    public bool IsAnonymous { get; set; }
    public bool AutoStart { get; set; } = true;
    public DateTimeOffset? StartedAt { get; set; }
    public string[] ModuleNames { get; set; } = [];
    public string[] Components { get; set; } = [];
    public int RouteCount { get; set; }
    public int EndpointCount { get; set; }
    public Dictionary<string, TsakRouteSnapshot> Routes { get; set; } = new();
    public Dictionary<string, TsakEndpointSnapshot> Endpoints { get; set; } = new();
}

/// <summary>
/// EAV-compatible route snapshot for heartbeat propagation.
/// </summary>
public class TsakRouteSnapshot
{
    public string RouteId { get; set; } = string.Empty;
    public string FromUri { get; set; } = string.Empty;
    public string Status { get; set; } = "Stopped";
    public bool AutoStart { get; set; } = true;
}

/// <summary>
/// EAV-compatible endpoint snapshot for heartbeat propagation.
/// </summary>
public class TsakEndpointSnapshot
{
    public string Uri { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public bool IsConsumer { get; set; }
    public bool IsProducer { get; set; }
    public long MessagesIn { get; set; }
    public long MessagesOut { get; set; }
    public long Errors { get; set; }
    public long Warnings { get; set; }
    public long BytesIn { get; set; }
    public double ThroughputPerSecond { get; set; }
    public string HealthStatus { get; set; } = "Healthy";
    public string? HealthReason { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public double UptimeSeconds { get; set; }
    public double AverageProcessingTimeMs { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTimeOffset? LastErrorTime { get; set; }
    public string? LastWarningMessage { get; set; }
    public DateTimeOffset? LastWarningTime { get; set; }
}
