using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Monitoring;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// REST API for the lifecycle event audit trail.
/// Supports filtering by context, route, event type, and time.
/// </summary>
[Route("/api/lifecycle")]
public class LifecycleController : RedbController
{
    private LifecycleAuditService GetAudit() => Context.GetService<LifecycleAuditService>()
        ?? throw new InvalidOperationException("LifecycleAuditService not registered in context");

    [HttpGet("")]
    public object GetEvents(
        [FromQuery("context")] string? contextName = null,
        [FromQuery("route")] string? routeId = null,
        [FromQuery("type")] string? eventType = null,
        [FromQuery("since")] string? since = null,
        [FromQuery("limit")] int? limit = null)
    {
        LifecycleEventType? parsedType = null;
        if (eventType is not null && Enum.TryParse<LifecycleEventType>(eventType, true, out var t))
            parsedType = t;

        DateTime? parsedSince = null;
        if (since is not null && DateTime.TryParse(since, out var s))
            parsedSince = s.ToUniversalTime();

        return GetAudit().GetEvents(
            contextName: contextName,
            routeId: routeId,
            eventType: parsedType,
            since: parsedSince,
            limit: limit ?? 100);
    }
}
