using redb.Route.Abstractions;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// Context-level error handler (installed as <c>RouteContext.ErrorHandler</c>, Tier-3 in
/// <c>RouteContext.HandleException</c>) that dead-letters a failed exchange when the route carried a
/// replay checkpoint. Reads <c>route.checkpoint</c> off the exchange and hands it to
/// <see cref="DlqService"/> for durable capture.
/// <para>
/// Capture is opt-in by construction: only routes with a <c>.Replayable("…")</c> marker leave a
/// checkpoint, so only those are captured. Routes without a checkpoint fall through — the DLQ never
/// tries to own an exchange the route author did not mark as replayable (and Route already warns if a
/// marker sits on a broker/transacted route, where the broker/transaction owns redelivery).
/// </para>
/// </summary>
public sealed class CheckpointDlqHandler : IErrorHandler
{
    private readonly string _contextName;
    private readonly DlqService _dlq;

    public CheckpointDlqHandler(string contextName, DlqService dlq)
    {
        _contextName = contextName;
        _dlq = dlq;
    }

    public async Task HandleError(IExchange exchange, Exception exception, CancellationToken ct = default)
    {
        if (exchange.Properties.TryGetValue(RouteCheckpoint.PropertyKey, out var v) &&
            v is RouteCheckpoint checkpoint)
        {
            await _dlq.CaptureAsync(_contextName, checkpoint, exception, ct).ConfigureAwait(false);
        }
        // No checkpoint → nothing to dead-letter here (Tier-4 rethrow governs the rest).
    }
}
