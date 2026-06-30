using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Default <see cref="IAdminAuditService"/> implementation that emits each event
/// as a structured warning-level log entry under the <c>Tsak.AdminAudit</c> source context.
/// <para>
/// Use this when no persistent audit store is configured. Pro ships a redb-backed
/// replacement that stores events as <c>RedbObject</c>s for queryable history.
/// </para>
/// </summary>
public sealed class LogAdminAuditService : IAdminAuditService
{
    private readonly ILogger<LogAdminAuditService> _logger;

    public LogAdminAuditService(ILogger<LogAdminAuditService> logger)
    {
        _logger = logger;
    }

    public Task RecordAsync(AdminAuditEvent @event, CancellationToken ct)
    {
        // [admin-audit] tag for log routing/filtering. WRN level so events stand out
        // even if the application is configured for INF baseline.
        _logger.LogWarning(
            "[admin-audit] {Action} by {Actor}@{RemoteIp} target={Target} status={Status} dur={DurationMs:F1}ms ex={ExceptionType}",
            @event.Action,
            @event.ActorPrincipalName ?? "<anonymous>",
            @event.RemoteIp ?? "<unknown>",
            @event.TargetResource ?? "<none>",
            @event.StatusCode,
            @event.DurationMs,
            @event.ExceptionType ?? "<none>");

        return Task.CompletedTask;
    }
}
