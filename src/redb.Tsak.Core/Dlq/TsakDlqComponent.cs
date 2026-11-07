using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// The checkpoint DLQ as an endpoint. Scheme: <c>tsak-dlq</c>; URI: <c>tsak-dlq:</c> (no path, no options).
/// <para>
/// The context's <see cref="CheckpointDlqHandler"/> sees only the failures no <c>OnException</c> took — Apache
/// Camel's model: an <c>OnException</c> that takes a failure owns it, and a logging one without <c>Handled()</c>
/// still keeps it from the DLQ. Sending to this endpoint from the handler's body dead-letters the failure
/// explicitly, as <c>onException(...).to(deadLetterUri)</c> does in Camel:
/// <code>
/// OnException&lt;HttpRequestException&gt;().Log("upstream down").To("tsak-dlq:");
/// </code>
/// The capture is the one the handler makes: the route must have left a replay checkpoint (<c>.Replayable("…")</c>
/// before the failing step); without one nothing is captured and a warning names the route.
/// </para>
/// Registered by Tsak on every context it creates while a DLQ service is configured.
/// </summary>
public sealed class TsakDlqComponent : ComponentBase
{
    /// <summary>The scheme of the endpoint, <c>tsak-dlq</c>.</summary>
    public const string SchemeName = "tsak-dlq";

    private readonly DlqService _dlq;

    public TsakDlqComponent(string contextName, DlqService dlq)
    {
        ContextName = contextName;
        _dlq = dlq;
    }

    /// <inheritdoc />
    public override string Scheme => SchemeName;

    /// <summary>The Tsak context name the captured entries are filed under.</summary>
    public string ContextName { get; }

    internal DlqService Dlq => _dlq;

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.IsNullOrEmpty(uri.Path))
            throw new ArgumentException(
                $"'{EndpointUri.Sanitize(uri.NormalizedKey)}': the DLQ of the context takes no path — write tsak-dlq:", nameof(uri));
        var options = new TsakDlqEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return new TsakDlqEndpoint(uri, this, options);
    }
}

/// <summary>Options of <c>tsak-dlq:</c> — none.</summary>
public sealed class TsakDlqEndpointOptions : EndpointOptions
{
    /// <inheritdoc />
    public override void Validate() { }
}

/// <summary>The <c>tsak-dlq:</c> endpoint: a producer only.</summary>
public sealed class TsakDlqEndpoint : EndpointBase<TsakDlqEndpointOptions>
{
    public TsakDlqEndpoint(EndpointUri uri, TsakDlqComponent component, TsakDlqEndpointOptions options)
        : base(uri, component, options)
    {
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new TsakDlqProducer((TsakDlqComponent)Component);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => throw new NotSupportedException("tsak-dlq: is a destination — use it in To(), from an OnException body.");
}

/// <summary>Dead-letters the failure the exchange carries, from its replay checkpoint.</summary>
public sealed class TsakDlqProducer : IProducer
{
    private readonly TsakDlqComponent _component;

    public TsakDlqProducer(TsakDlqComponent component) => _component = component;

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // An OnException body runs with the failure on the exchange; anywhere else there is nothing to dead-letter.
        var failure = exchange.Exception
            ?? throw new InvalidOperationException(
                $"Route '{exchange.RouteId}': tsak-dlq: dead-letters a failure — send to it from an OnException body.");

        if (!await CheckpointDlqHandler.CaptureAsync(_component.ContextName, _component.Dlq, exchange, failure, ct)
                .ConfigureAwait(false))
        {
            _component.Logger?.LogWarning(
                "Route '{RouteId}': tsak-dlq: received a failure but the route left no replay checkpoint — nothing was " +
                "captured. Mark the route .Replayable(\"…\") before the step that can fail.",
                exchange.RouteId);
        }
    }

    /// <inheritdoc />
    public Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
}
