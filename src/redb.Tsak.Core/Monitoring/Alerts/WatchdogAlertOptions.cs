namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// Alert delivery configuration, bound from <c>Tsak:Watchdog:Alerts</c>.
/// <para>
/// Everything is <b>off by default</b>: no channel delivers until it is explicitly enabled and
/// given its connection parameters. Enabling <see cref="Enabled"/> alone does nothing — at least
/// one channel must also be enabled.
/// </para>
/// </summary>
public sealed class WatchdogAlertOptions
{
    /// <summary>Master switch. Default: <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Minimum severity that triggers delivery: <c>"Suspected"</c> or <c>"Hung"</c>.
    /// Default: <c>"Hung"</c> — only genuinely stuck exchanges page someone.
    /// </summary>
    public string MinLevel { get; set; } = "Hung";

    /// <summary>
    /// Suppression window: the same alert (context+route+exchange+level) is delivered at most once
    /// per this many minutes, so a persistently hung exchange does not re-page every scan.
    /// Default: 15.
    /// </summary>
    public double DedupWindowMinutes { get; set; } = 15;

    public WebhookChannelOptions Webhook { get; set; } = new();
    public TelegramChannelOptions Telegram { get; set; } = new();
    public EmailChannelOptions Email { get; set; } = new();
    public EndpointChannelOptions Endpoint { get; set; } = new();
}

/// <summary>Generic outgoing webhook — POSTs the alert JSON to a URL (Slack/Teams/PagerDuty/…).</summary>
public sealed class WebhookChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>Target URL. Required when enabled.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional extra headers (e.g. an auth token).</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>Telegram Bot API — a formatted HTTPS POST, no connector needed.</summary>
public sealed class TelegramChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>Bot token from @BotFather. Required when enabled.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Target chat id. Required when enabled.</summary>
    public string ChatId { get; set; } = "";
}

/// <summary>Email via SMTP (BCL SmtpClient — no extra package).</summary>
public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// Generic route endpoint — sends the alert to any redb.Route URI (<c>kafka:topic</c>,
/// <c>amqp:queue</c>, <c>mqtt:…</c>) via a ProducerTemplate. Works only when the matching
/// component is registered in the host's _system context; it is never a compile-time dependency
/// of the Tsak kernel, so brokers add no weight to Core.
/// </summary>
public sealed class EndpointChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>Route producer URI, e.g. <c>kafka:tsak-alerts</c>. Required when enabled.</summary>
    public string Uri { get; set; } = "";
}
