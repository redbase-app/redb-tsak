using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Audit;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Query the persistent admin audit trail.
/// <c>GET /api/audit</c> — filtered, paged, newest-first.
/// <para>
/// Reading the audit is an <c>admin</c> operation: the trail records who did what, which is
/// exactly the information a lower-privilege caller must not be able to enumerate.
/// </para>
/// </summary>
[Route("/api/audit")]
[RequiresRole(TsakRoles.Admin)]
public class AuditController : RedbController
{
    /// <summary>
    /// Returns audit entries matching the optional filters.
    /// </summary>
    /// <param name="actor">Match on API key id or principal name.</param>
    /// <param name="action">Logical action name (e.g. <c>RemoveContext</c>).</param>
    /// <param name="target">Target resource identifier.</param>
    /// <param name="since">Lower time bound (inclusive), ISO-8601.</param>
    /// <param name="until">Upper time bound (inclusive), ISO-8601.</param>
    /// <param name="limit">Page size, 1..1000 (default 100).</param>
    /// <param name="offset">Rows to skip (default 0).</param>
    [HttpGet("")]
    public async Task<object> Query(
        [FromQuery("actor")] string? actor,
        [FromQuery("action")] string? action,
        [FromQuery("target")] string? target,
        [FromQuery("since")] string? since,
        [FromQuery("until")] string? until,
        [FromQuery("limit")] int? limit,
        [FromQuery("offset")] int? offset)
    {
        var svc = Context.GetService<AuditQueryService>();
        if (svc is null)
            return new AuditQueryResult { Available = false, Error = "AuditQueryService not available", Entries = [] };

        DateTimeOffset? sinceTs = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilTs = DateTimeOffset.TryParse(until, out var u) ? u : null;

        return await svc.QueryAsync(
            actor: actor,
            action: action,
            target: target,
            since: sinceTs,
            until: untilTs,
            limit: limit ?? 100,
            offset: offset ?? 0);
    }
}
