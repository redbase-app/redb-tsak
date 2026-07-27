using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Generic route-endpoint channel: sends the alert JSON to any redb.Route producer URI
/// (<c>kafka:topic</c>, <c>amqp:queue</c>, <c>mqtt:…</c>) via a <see cref="ProducerTemplate"/>.
/// <para>
/// This is how brokers are supported without Core taking a compile-time dependency on any broker
/// connector: the target component must be registered in the host's _system context (Tsak.Worker
/// loads connectors as shared assemblies at runtime). If it is not, the send fails and the
/// dispatcher logs it — the same as any other misconfigured channel.
/// </para>
/// </summary>
public sealed class EndpointAlertChannel : IAlertChannel, IDisposable
{
    private readonly EndpointChannelOptions _options;
    private readonly ProducerTemplate? _producer;

    /// <param name="context">
    /// The _system route context, or <c>null</c> when alerts run outside a route context (then the
    /// channel is simply never enabled).
    /// </param>
    public EndpointAlertChannel(EndpointChannelOptions options, IRouteContext? context)
    {
        _options = options;
        if (context is not null && options.Enabled && !string.IsNullOrWhiteSpace(options.Uri))
        {
            _producer = new ProducerTemplate(context);
            if (!_producer.IsStarted) _producer.Start();
        }
    }

    public string Name => "endpoint";

    public bool IsEnabled => _producer is not null;

    public async Task SendAsync(AlertNotification alert, CancellationToken ct)
    {
        if (_producer is null)
            throw new InvalidOperationException("Endpoint channel is not configured.");

        var msg = new Message { Body = alert.ToJson() };
        msg.Headers["alert.level"] = alert.Level;
        msg.Headers["Content-Type"] = "application/json";
        await _producer.SendAsync(_options.Uri, msg).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { if (_producer is { IsStarted: true }) _producer.Stop(); }
        catch { /* best effort on shutdown */ }
    }
}
