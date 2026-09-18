namespace redb.Tsak.Web.Services;

/// <summary>
/// Shared body of the pages' auto-refresh loops (review 2026-09-02, С18): one transient
/// exception (a node timeout, a DB blip, a JS-interop disconnect) must not silently kill the
/// fire-and-forget loop — the page would keep showing stale data under a live "Auto-refresh"
/// label for the rest of the circuit. A failed tick is logged and dropped, and the next tick
/// retries; only the loop's OWN cancellation ends it.
/// <para>
/// Threading contract: <c>tick</c> runs on the thread pool, NOT on the Blazor dispatcher. A tick
/// that touches component state or calls StateHasChanged (its own or a child's) must wrap its
/// body in the component's InvokeAsync.
/// </para>
/// </summary>
public static class PollLoops
{
    public static async Task RunAsync(PeriodicTimer timer, CancellationToken ct, ILogger log, Func<Task> tick)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await tick().ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // The page is being disposed; whatever the in-flight tick threw is moot.
                    break;
                }
                catch (Exception ex)
                {
                    // Includes OperationCanceledException from a FOREIGN token: an HttpClient
                    // timeout is a TaskCanceledException, and the old catch-and-break on any
                    // cancellation turned one slow node response into auto-refresh dead for the
                    // rest of the circuit. Warning, not Debug: a failed tick means stale data on
                    // screen, and the silent catch that used to be here hid a Dashboard failure
                    // on every tick for twelve days.
                    log.LogWarning(ex, "Poll tick failed; the page keeps its last data and retries on the next tick");
                }
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }
}
