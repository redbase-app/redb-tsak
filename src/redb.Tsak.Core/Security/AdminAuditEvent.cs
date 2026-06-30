namespace redb.Tsak.Core.Security;

/// <summary>
/// Snapshot of a single audited admin operation.
/// Produced by <see cref="AdminAuditFilter"/> and dispatched to <see cref="IAdminAuditService"/>.
/// All fields are immutable post-construction.
/// </summary>
public sealed class AdminAuditEvent
{
    /// <summary>UTC timestamp at which the action completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Logical action name (e.g. <c>RestartContext</c>, <c>RevokeKey</c>).</summary>
    public required string Action { get; init; }

    /// <summary>Controller type that owns the action.</summary>
    public required string ControllerType { get; init; }

    /// <summary>Resolved actor (principal) name from the API key, if available.</summary>
    public string? ActorPrincipalName { get; init; }

    /// <summary>API key identifier used for the request, if available.</summary>
    public string? ActorApiKeyId { get; init; }

    /// <summary>Remote IP address of the caller, if available.</summary>
    public string? RemoteIp { get; init; }

    /// <summary>Request User-Agent header, if available.</summary>
    public string? UserAgent { get; init; }

    /// <summary>HTTP method (GET, POST, …).</summary>
    public string? HttpMethod { get; init; }

    /// <summary>Request path.</summary>
    public string? RequestPath { get; init; }

    /// <summary>Identifier of the target resource (resolved from <c>TargetParam</c>), if any.</summary>
    public string? TargetResource { get; init; }

    /// <summary>Sanitized JSON payload of the action arguments (sensitive fields redacted).</summary>
    public string? Payload { get; init; }

    /// <summary>HTTP-style status code returned by the action.</summary>
    public int StatusCode { get; init; }

    /// <summary>Action invocation duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Exception type name, if the action threw.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Exception message, if the action threw.</summary>
    public string? ExceptionMessage { get; init; }
}

/// <summary>
/// SPI for delivering admin audit events to a sink. Default implementation is
/// <see cref="LogAdminAuditService"/> (Serilog/ILogger). Pro replaces this with
/// a redb-backed implementation that persists events to <c>RedbObject</c>s.
/// <para>
/// Implementations MUST NOT throw — failures must be logged internally; the audit
/// pipeline is best-effort and must not affect the primary action result.
/// </para>
/// </summary>
public interface IAdminAuditService
{
    Task RecordAsync(AdminAuditEvent @event, CancellationToken ct);
}
