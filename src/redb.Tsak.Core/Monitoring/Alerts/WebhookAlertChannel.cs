using System.Net.Http.Headers;
using System.Text;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Generic webhook: POSTs the alert JSON to a configured URL. Covers Slack / Teams / Mattermost /
/// PagerDuty and any HTTP collector. Uses a plain <see cref="HttpClient"/> — no route component,
/// no extra dependency.
/// </summary>
public sealed class WebhookAlertChannel : IAlertChannel
{
    private readonly WebhookChannelOptions _options;
    private readonly HttpClient _http;

    public WebhookAlertChannel(WebhookChannelOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public string Name => "webhook";

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Url);

    public async Task SendAsync(AlertNotification alert, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            Content = new StringContent(alert.ToJson(), Encoding.UTF8, "application/json")
        };
        foreach (var (key, value) in _options.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
