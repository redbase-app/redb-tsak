using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Fans watchdog / lifecycle alerts out to every enabled <see cref="IAlertChannel"/>.
/// <para>
/// <b>Fire-and-forget:</b> alerts go through a bounded in-memory queue drained by a background
/// pump, so the watchdog scan never blocks on a slow SMTP server, and a broken alert backend can
/// never take the node down. Queue overflow drops the oldest (with a warning).
/// </para>
/// <para>
/// <b>Dedup:</b> the same incident (<see cref="AlertNotification.DedupKey"/>) is delivered at most
/// once per <see cref="WatchdogAlertOptions.DedupWindowMinutes"/> — the watchdog rebuilds its alert
/// snapshot every scan, so without this a hung exchange would re-page on every cycle.
/// </para>
/// </summary>
public sealed class AlertDispatcher : IAsyncDisposable
{
    private const int QueueCapacity = 500;

    private readonly WatchdogAlertOptions _options;
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly Channel<AlertNotification> _queue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private readonly int _minRank;

    // Copy-on-write so the pump reads a stable snapshot while the endpoint channel may be
    // attached later (it needs the _system route context, which exists only after startup).
    private volatile IReadOnlyList<IAlertChannel> _channels;

    public AlertDispatcher(
        WatchdogAlertOptions options,
        IEnumerable<IAlertChannel> channels,
        ILogger<AlertDispatcher> logger)
    {
        _options = options;
        _channels = channels.Where(c => c.IsEnabled).ToList();
        _logger = logger;
        _minRank = RankOf(options.MinLevel);
        _queue = Channel.CreateBounded<AlertNotification>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _pump = Task.Run(PumpAsync);

        if (IsActive)
            _logger.LogInformation("Alert delivery enabled — channels: {Channels}, min level: {MinLevel}",
                string.Join(", ", _channels.Select(c => c.Name)), options.MinLevel);
    }

    /// <summary>
    /// Attaches the generic broker endpoint channel once the _system route context is available.
    /// No-op unless the endpoint channel is enabled and resolves a producer.
    /// </summary>
    public void AttachEndpointChannel(redb.Route.Abstractions.IRouteContext context)
    {
        var endpoint = new EndpointAlertChannel(_options.Endpoint, context);
        if (!endpoint.IsEnabled) { endpoint.Dispose(); return; }

        _channels = _channels.Append(endpoint).ToList(); // copy-on-write
        _logger.LogInformation("Alert endpoint channel attached → {Uri}", _options.Endpoint.Uri);
    }

    /// <summary>True when alerts are on and at least one channel is enabled.</summary>
    public bool IsActive => _options.Enabled && _channels.Count > 0;

    /// <summary>Names of the enabled channels (for status display).</summary>
    public IReadOnlyList<string> EnabledChannels => _channels.Select(c => c.Name).ToList();

    /// <summary>
    /// Enqueues an alert for delivery, applying the min-level filter and dedup window. Never blocks.
    /// </summary>
    public void Enqueue(AlertNotification alert)
    {
        if (!IsActive) return;
        if (RankOf(alert.Level) < _minRank) return;

        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(_options.DedupWindowMinutes);
        var last = _lastSent.GetOrAdd(alert.DedupKey, DateTimeOffset.MinValue);
        if (now - last < window) return; // still inside the suppression window

        _lastSent[alert.DedupKey] = now;
        _queue.Writer.TryWrite(alert); // DropOldest → never fails, never blocks
    }

    /// <summary>
    /// Delivers an alert to every enabled channel <b>synchronously</b>, bypassing the queue and the
    /// dedup window, and returns the per-channel outcome. Used by <c>POST /api/watchdog/test-alert</c>
    /// so an operator can verify configuration and see exactly which channel failed and why.
    /// </summary>
    public async Task<IReadOnlyList<AlertChannelResult>> TestAsync(AlertNotification alert, CancellationToken ct)
    {
        var results = new List<AlertChannelResult>();
        foreach (var channel in _channels)
        {
            try
            {
                await channel.SendAsync(alert, ct).ConfigureAwait(false);
                results.Add(new AlertChannelResult(channel.Name, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new AlertChannelResult(channel.Name, false, ex.Message));
            }
        }
        return results;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                foreach (var channel in _channels)
                {
                    try
                    {
                        await channel.SendAsync(alert, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Alert channel {Channel} failed to deliver {Level} alert",
                            channel.Name, alert.Level);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private static int RankOf(string level) => level?.Trim().ToLowerInvariant() switch
    {
        "suspected" => 1,
        "hung" => 2,
        "test" => 0, // test alerts pass the filter regardless of MinLevel
        _ => 2
    };

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* drain best-effort */ }
        await _shutdown.CancelAsync();
        _shutdown.Dispose();

        foreach (var channel in _channels)
            (channel as IDisposable)?.Dispose();
    }
}

/// <summary>Per-channel outcome of a test delivery.</summary>
public sealed record AlertChannelResult(string Channel, bool Success, string? Error);
