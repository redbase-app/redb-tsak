using System.Text;
using System.Text.Json;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Telegram delivery via the Bot API — a formatted HTTPS POST to
/// <c>https://api.telegram.org/bot{token}/sendMessage</c>. Intentionally uses a plain
/// <see cref="HttpClient"/> rather than a Telegram connector, so it adds nothing to Core's graph.
/// </summary>
public sealed class TelegramAlertChannel : IAlertChannel
{
    private readonly TelegramChannelOptions _options;
    private readonly HttpClient _http;

    public TelegramAlertChannel(TelegramChannelOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public string Name => "telegram";

    public bool IsEnabled =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.BotToken)
        && !string.IsNullOrWhiteSpace(_options.ChatId);

    public async Task SendAsync(AlertNotification alert, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
        var payload = JsonSerializer.Serialize(new
        {
            chat_id = _options.ChatId,
            text = alert.ToText()
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
