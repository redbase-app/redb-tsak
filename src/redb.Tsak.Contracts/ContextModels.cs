namespace redb.Tsak.Contracts;

public sealed record ContextSummary
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required int EndpointCount { get; init; }
    public bool IsAnonymous { get; init; }
    public int RouteCount { get; init; }
}

public sealed record ContextDetail
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required int EndpointCount { get; init; }
    public required bool AutoStart { get; init; }
    public bool IsAnonymous { get; init; }
    public string[] Components { get; init; } = [];
    public TsakRouteInfo[] Routes { get; init; } = [];
    public TsakEndpointInfo[] Endpoints { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
}

public sealed record ContextActionResponse
{
    public required string Name { get; init; }
    public string? Status { get; init; }
    public bool? Removed { get; init; }

    /// <summary>In-flight exchange count at the moment stop/restart was initiated.</summary>
    public int? InflightBefore { get; init; }

    /// <summary>In-flight exchange count after drain completed (0 = fully drained).</summary>
    public int? InflightAfter { get; init; }

    /// <summary>Time spent draining in-flight exchanges (ms).</summary>
    public long? DrainTimeMs { get; init; }
}

public sealed record TsakRouteInfo
{
    public required string RouteId { get; init; }
    public required string FromUri { get; init; }
    public required string Status { get; init; }
    public bool AutoStart { get; init; }
    public int InflightCount { get; init; }

    /// <summary>True if the route is managed by a cluster policy (distributed lock).</summary>
    public bool IsCluster { get; init; }

    /// <summary>
    /// Effective cluster-policy resolution captured at route compile time. Surfaces
    /// whether <c>.Cluster(true)</c> was requested and which policy (if any) was attached.
    /// May be <c>null</c> on older Route runtimes that do not expose the descriptor yet.
    /// </summary>
    public RoutePolicyInfo? Policy { get; init; }
}

/// <summary>
/// Compact projection of <c>RoutePolicyDescriptor</c> for API serialization, decoupled
/// from the <c>redb.Route</c> assembly to keep <c>redb.Tsak.Contracts</c> dependency-free.
/// </summary>
public sealed record RoutePolicyInfo
{
    /// <summary>True if the route declared <c>.Cluster(true)</c>.</summary>
    public required bool RequestedCluster { get; init; }

    /// <summary>One of: <c>"AllNodes"</c>, <c>"ClusterLeader"</c>, <c>"Custom"</c>.</summary>
    public required string EffectivePolicy { get; init; }

    /// <summary>Fully-qualified factory type name, or null when no factory was used.</summary>
    public string? PolicyFactoryType { get; init; }

    /// <summary>Human-readable explanation.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Detailed per-route information including inflight exchanges.
/// Returned by GET /api/contexts/{ctx}/routes/{routeId}.
/// </summary>
public sealed record RouteDetail
{
    public required string RouteId { get; init; }
    public required string FromUri { get; init; }
    public required string Status { get; init; }
    public bool AutoStart { get; init; }
    public int InflightCount { get; init; }
    public TsakEndpointInfo[] Endpoints { get; init; } = [];

    /// <summary>Policy-specific metadata (e.g., cluster/leader info). Null if no policy.</summary>
    public IReadOnlyDictionary<string, object>? PolicyMetadata { get; init; }
}

/// <summary>
/// Snapshot of an in-flight exchange exposed via the REST API.
/// </summary>
public sealed record InflightExchangeDto
{
    public required string ExchangeId { get; init; }
    public required string RouteId { get; init; }
    public required DateTime StartedAt { get; init; }
    public required double ElapsedMs { get; init; }
    public required int ThreadId { get; init; }
    public string? FromEndpoint { get; init; }
}

/// <summary>
/// Response for route start/stop operations.
/// </summary>
public sealed record RouteActionResponse
{
    public required string RouteId { get; init; }
    public required string Status { get; init; }
}

public sealed record TsakEndpointInfo
{
    public required string Uri { get; init; }
    public required string Scheme { get; init; }
    public bool IsConsumer { get; init; }
    public bool IsProducer { get; init; }
    public long MessagesIn { get; init; }
    public long MessagesOut { get; init; }
    public long Errors { get; init; }
    public long Warnings { get; init; }
    public long BytesIn { get; init; }
    public double ThroughputPerSecond { get; init; }
    public string HealthStatus { get; init; } = "Healthy";
    public string? HealthReason { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public TimeSpan Uptime { get; init; }
    public TimeSpan AverageProcessingTime { get; init; }
    public string? LastErrorMessage { get; init; }
    public DateTimeOffset? LastErrorTime { get; init; }
    public string? LastWarningMessage { get; init; }
    public DateTimeOffset? LastWarningTime { get; init; }
}
