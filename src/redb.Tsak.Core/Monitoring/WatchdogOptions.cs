namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Configuration for the Route Watchdog background service.
/// Binds to the <c>Tsak:Watchdog</c> section in appsettings.json.
/// </summary>
public class WatchdogOptions
{
    /// <summary>Check interval in seconds. Default: 15.</summary>
    public int CheckIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Threshold in minutes for a "suspected" exchange. Default: 5.
    /// Exchanges older than this emit a WARNING. Supports fractional values (e.g. 0.5 = 30s).
    /// </summary>
    public double SuspectedThresholdMinutes { get; set; } = 5;

    /// <summary>
    /// Threshold in minutes for a "hung" exchange. Default: 30.
    /// Exchanges older than this emit an ERROR and trigger alerts. Supports fractional values.
    /// </summary>
    public double HungThresholdMinutes { get; set; } = 30;

    /// <summary>
    /// Whether to auto-restart routes with hung exchanges. Default: false.
    /// </summary>
    public bool AutoRestartHungRoutes { get; set; }

    /// <summary>
    /// Timeout in seconds for auto-restart operations. Default: 60.
    /// </summary>
    public int AutoRestartTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether the watchdog is enabled. Default: true.
    /// Can be toggled at runtime via the API.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
