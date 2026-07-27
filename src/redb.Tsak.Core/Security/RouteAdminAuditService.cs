using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Core.Audit;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Persistent admin audit sink: hands each event to the <c>direct://tsak-audit</c> route,
/// which writes it into <c>tsak_audit_log</c>.
/// <para>
/// <b>Fire-and-forget by design.</b> Events go through a bounded in-memory queue drained by a
/// background pump, so an API call never waits for the database and a broken audit backend can
/// never take the node down. The trade-off is explicit: on a hard kill the queued tail is lost,
/// and under a sustained flood the oldest queued events are dropped (with a warning) rather
/// than growing memory without bound.
/// </para>
/// <para>
/// Until <see cref="Attach"/> is called — and permanently when Tsak runs without a database —
/// events fall through to <paramref name="fallback"/>, the log sink.
/// </para>
/// </summary>
public sealed class RouteAdminAuditService : IAdminAuditService, IAsyncDisposable
{
    /// <summary>Queue depth before the oldest events start being dropped.</summary>
    private const int QueueCapacity = 1000;

    private readonly IAdminAuditService _fallback;
    private readonly ILogger<RouteAdminAuditService> _logger;
    private readonly Channel<AdminAuditEvent> _queue;
    private readonly CancellationTokenSource _shutdown = new();

    private ProducerTemplate? _producer;
    private Task? _pump;
    private long _dropped;
    private bool _disposed;

    public RouteAdminAuditService(IAdminAuditService fallback, ILogger<RouteAdminAuditService> logger)
    {
        _fallback = fallback;
        _logger = logger;
        _queue = Channel.CreateBounded<AdminAuditEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    /// <summary>True once a route context is attached and events are being persisted.</summary>
    public bool IsPersisting => _producer is not null;

    /// <summary>
    /// Binds the sink to the route context that hosts the audit writer route. Called by
    /// <c>SystemContextBuilder</c> after the route is registered and the context is started.
    /// </summary>
    public void Attach(IRouteContext context)
    {
        if (_disposed || _producer is not null) return;

        var producer = new ProducerTemplate(context);
        if (!producer.IsStarted) producer.Start();
        _producer = producer;

        _pump = Task.Run(PumpAsync);
        _logger.LogInformation("Admin audit events are persisted via {Endpoint}", AuditStorage.AuditEndpoint);
    }

    /// <inheritdoc />
    public Task RecordAsync(AdminAuditEvent @event, CancellationToken ct)
    {
        if (_disposed || _producer is null)
            return _fallback.RecordAsync(@event, ct);

        // DropOldest never blocks and never fails — TryWrite returning false is not expected,
        // but treat it as a drop rather than losing the event silently.
        if (!_queue.Writer.TryWrite(@event))
            ReportDrop();

        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var @event in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    await _producer!.SendAsync(AuditStorage.AuditEndpoint, BuildMessage(@event));
                }
                catch (Exception ex)
                {
                    // The audit backend is unreachable or the table is missing. Keep the event
                    // visible through the log sink and carry on — never break the pump.
                    _logger.LogWarning(ex, "Failed to persist admin audit event {Action}", @event.Action);
                    await SafeFallbackAsync(@event);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SafeFallbackAsync(AdminAuditEvent @event)
    {
        try
        {
            await _fallback.RecordAsync(@event, CancellationToken.None);
        }
        catch
        {
            // The fallback is a logger; if even that throws there is nowhere left to report.
        }
    }

    private void ReportDrop()
    {
        var dropped = Interlocked.Increment(ref _dropped);

        // Log the first drop and then every thousandth — a flood must not turn into a log flood.
        if (dropped == 1 || dropped % 1000 == 0)
            _logger.LogWarning(
                "Admin audit queue is saturated — {Dropped} event(s) dropped. " +
                "The audit backend cannot keep up with the request rate.", dropped);
    }

    /// <summary>Maps an event onto the headers the writer route binds its parameters from.</summary>
    internal static IMessage BuildMessage(AdminAuditEvent @event)
    {
        var msg = new Message
        {
            // Body is the sanitized argument JSON; the payload column is nullable, but an
            // empty object keeps the column shape uniform for consumers.
            Body = string.IsNullOrWhiteSpace(@event.Payload) ? "{}" : @event.Payload
        };

        msg.Headers[AuditHeaders.EventId] = Guid.NewGuid().ToString();
        // Round-trip format ("o"): ISO-8601 with offset — accepted verbatim by all three
        // providers and lexicographically sortable in the SQLite TEXT column.
        msg.Headers[AuditHeaders.Timestamp] = @event.Timestamp.ToString("o", CultureInfo.InvariantCulture);
        msg.Headers[AuditHeaders.Action] = @event.Action;
        msg.Headers[AuditHeaders.ControllerType] = @event.ControllerType;
        msg.Headers[AuditHeaders.ActorPrincipal] = @event.ActorPrincipalName;
        msg.Headers[AuditHeaders.ActorKeyId] = @event.ActorApiKeyId;
        msg.Headers[AuditHeaders.RemoteIp] = @event.RemoteIp;
        msg.Headers[AuditHeaders.UserAgent] = Truncate(@event.UserAgent, 500);
        msg.Headers[AuditHeaders.HttpMethod] = @event.HttpMethod;
        msg.Headers[AuditHeaders.RequestPath] = Truncate(@event.RequestPath, 500);
        msg.Headers[AuditHeaders.TargetResource] = Truncate(@event.TargetResource, 300);
        msg.Headers[AuditHeaders.StatusCode] = @event.StatusCode;
        msg.Headers[AuditHeaders.DurationMs] = @event.DurationMs;
        msg.Headers[AuditHeaders.ExceptionType] = @event.ExceptionType;
        msg.Headers[AuditHeaders.ExceptionMessage] = Truncate(@event.ExceptionMessage, 2000);

        return msg;
    }

    /// <summary>Keeps values inside the declared column widths — an over-long User-Agent
    /// must not turn into a failed INSERT and a lost audit record.</summary>
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.Writer.TryComplete();

        try
        {
            // Give the pump a moment to drain what is already queued.
            if (_pump is not null)
                await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Admin audit pump did not drain cleanly on shutdown");
        }

        await _shutdown.CancelAsync();
        _shutdown.Dispose();

        try
        {
            if (_producer is { IsStarted: true }) _producer.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop the admin audit ProducerTemplate");
        }
    }
}
