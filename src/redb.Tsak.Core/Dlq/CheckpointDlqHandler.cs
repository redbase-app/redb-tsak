using redb.Route.Abstractions;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// Context-level error handler (installed as <c>RouteContext.ErrorHandler</c> when Tsak starts the context) that
/// dead-letters a failed exchange when the route carried a replay checkpoint. Reads <c>route.checkpoint</c> off the
/// exchange and hands it to <see cref="DlqService"/> for durable capture.
/// <para>
/// Capture is opt-in by construction: only routes with a <c>.Replayable("…")</c> marker leave a
/// checkpoint, so only those are captured. Routes without a checkpoint fall through — the DLQ never
/// tries to own an exchange the route author did not mark as replayable (and Route already warns if a
/// marker sits on a broker/transacted route, where the broker/transaction owns redelivery).
/// </para>
/// <para>
/// As the context's error handler it sees only the failures no <c>OnException</c> took — Apache Camel's model: an
/// <c>OnException</c> that takes a failure owns it; it can still dead-letter it explicitly with
/// <c>.To("tsak-dlq:")</c>.
/// </para>
/// <para>
/// Capture never marks the failure handled. When the module brought an error handler of its own
/// (<c>&lt;deadLetterChannel&gt;</c> in its <c>context.xml</c>, or one set in code) it is <see cref="Next"/>: the
/// failure is captured first, then the module's handler sends it on and decides whether it is handled.
/// </para>
/// </summary>
public sealed class CheckpointDlqHandler : IErrorHandler
{
    private readonly string _contextName;
    private readonly DlqService _dlq;

    public CheckpointDlqHandler(string contextName, DlqService dlq, IErrorHandler? next = null)
    {
        _contextName = contextName;
        _dlq = dlq;
        Next = next;
    }

    /// <summary>The error handler the module installed itself, run after the capture; <c>null</c> when none.</summary>
    public IErrorHandler? Next { get; }

    public async Task HandleError(IExchange exchange, Exception exception, CancellationToken ct = default)
    {
        await CaptureAsync(_contextName, _dlq, exchange, exception, ct).ConfigureAwait(false);

        if (Next is not null)
            await Next.HandleError(exchange, exception, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dead-letters the exchange when its route left a replay checkpoint; no checkpoint, nothing to capture (the
    /// failure goes on as it would without the DLQ). Shared with the <c>tsak-dlq:</c> endpoint.
    /// </summary>
    internal static async Task<bool> CaptureAsync(string contextName, DlqService dlq, IExchange exchange,
        Exception exception, CancellationToken ct)
    {
        if (exchange.Properties.TryGetValue(RouteCheckpoint.PropertyKey, out var v) && v is RouteCheckpoint checkpoint)
        {
            await dlq.CaptureAsync(contextName, checkpoint, exception, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }
}
