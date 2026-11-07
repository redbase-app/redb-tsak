namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// A single alert delivery destination (webhook, Telegram, email, broker endpoint).
/// Channels are resolved once at startup; only enabled ones are dispatched to.
/// </summary>
public interface IAlertChannel
{
    /// <summary>Short stable name, used in logs and the per-channel test result.</summary>
    string Name { get; }

    /// <summary>Whether this channel is turned on and fully configured.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Delivers one alert. Implementations must throw on failure (the dispatcher isolates and
    /// logs it); they must not swallow errors silently, or a misconfigured channel would look
    /// healthy.
    /// </summary>
    Task SendAsync(AlertNotification alert, CancellationToken ct);
}
