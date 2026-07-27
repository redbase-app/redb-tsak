using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Dlq;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Dead-letter queue: browse exchanges that failed at a route checkpoint, and replay or discard them.
/// <c>GET  /api/exchanges/failed</c> — filtered, paged list.
/// <c>POST /api/exchanges/{id}/replay</c> — re-run the entry's route tail from its snapshot.
/// <c>DELETE /api/exchanges/{id}</c> — discard.
/// <para>
/// Replay re-executes business code, so mutations require <c>operator</c>; discard requires <c>admin</c>.
/// </para>
/// </summary>
[Route("/api/exchanges")]
public class ExchangesController : RedbController
{
    private DlqService? GetDlq() => Context.GetService<DlqService>();

    [HttpGet("/failed")]
    [RequiresRole(TsakRoles.Operator)]
    public async Task<object> GetFailed(
        [FromQuery("context")] string? context,
        [FromQuery("route")] string? route,
        [FromQuery("status")] string? status,
        [FromQuery("since")] string? since,
        [FromQuery("until")] string? until,
        [FromQuery("limit")] int? limit,
        [FromQuery("offset")] int? offset)
    {
        var dlq = GetDlq();
        if (dlq is null)
            return new FailedExchangeQueryResult { Available = false, Error = "DLQ not available", Entries = [] };

        DateTimeOffset? sinceTs = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilTs = DateTimeOffset.TryParse(until, out var u) ? u : null;

        return await dlq.QueryAsync(
            context: context, route: route, status: status,
            since: sinceTs, until: untilTs,
            limit: limit ?? 100, offset: offset ?? 0);
    }

    [HttpPost("/{id}/replay")]
    [RequiresRole(TsakRoles.Operator)]
    [AuditAdminAction(ActionName = "ReplayExchange", TargetParam = "id")]
    public async Task<object> Replay([FromRoute("id")] string id)
    {
        var dlq = GetDlq();
        if (dlq is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "DLQ not available");
            Exchange.Stop();
            return null!;
        }

        var result = await dlq.ReplayAsync(id, CancellationToken.None);
        if (!result.Success)
        {
            ApiResponse.BadRequest(Exchange, result.Message);
            Exchange.Stop();
            return null!;
        }
        return result;
    }

    [HttpDelete("/{id}")]
    [RequiresRole(TsakRoles.Admin)]
    [AuditAdminAction(ActionName = "DiscardExchange", TargetParam = "id")]
    public async Task<object> Discard([FromRoute("id")] string id)
    {
        var dlq = GetDlq();
        if (dlq is null)
        {
            ApiResponse.ServiceUnavailable(Exchange, "DLQ not available");
            Exchange.Stop();
            return null!;
        }

        var removed = await dlq.DiscardAsync(id, CancellationToken.None);
        if (!removed)
        {
            ApiResponse.NotFound(Exchange, $"Entry '{id}' not found.");
            Exchange.Stop();
            return null!;
        }
        return new ExchangeReplayResult { Success = true, Message = "Discarded.", EntryId = id };
    }
}
