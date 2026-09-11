namespace redb.Tsak.Web.Services;

/// <summary>
/// Shared body of the pages' auto-refresh loops (review 2026-09-02, С18): one transient
/// exception (a node timeout, a DB blip, a JS-interop disconnect) must not silently kill the
/// fire-and-forget loop — the page would keep showing stale data under a live "Auto-refresh"
/// label for the rest of the circuit. A failed tick is dropped and the next tick retries;
/// cancellation ends the loop.
/// </summary>
public static class PollLoops
{
    public static async Task RunAsync(PeriodicTimer timer, CancellationToken ct, Func<Task> tick)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await tick().ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { /* retried on the next tick */ }
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }
}
