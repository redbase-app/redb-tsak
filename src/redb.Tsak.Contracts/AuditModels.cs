namespace redb.Tsak.Contracts;

/// <summary>
/// Response for <c>GET /api/audit</c>: a newest-first page of admin audit entries.
/// <see cref="Available"/> is <c>false</c> when Tsak runs without a database — in that mode
/// the audit trail lives in the log, not in a queryable table.
/// </summary>
public sealed record AuditQueryResult
{
    public required bool Available { get; init; }
    public int? Count { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public string? Error { get; init; }
    public AuditEntry[] Entries { get; init; } = [];
}

/// <summary>A single persisted admin audit entry.</summary>
public sealed record AuditEntry
{
    public required string EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Action { get; init; }
    public string? ControllerType { get; init; }
    public string? ActorPrincipal { get; init; }
    public string? ActorKeyId { get; init; }
    public string? RemoteIp { get; init; }
    public string? UserAgent { get; init; }
    public string? HttpMethod { get; init; }
    public string? RequestPath { get; init; }
    public string? TargetResource { get; init; }
    public int StatusCode { get; init; }
    public double DurationMs { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? Payload { get; init; }
}
