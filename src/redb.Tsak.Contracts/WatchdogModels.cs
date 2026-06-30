namespace redb.Tsak.Contracts;

/// <summary>
/// Snapshot of the Route Watchdog state, returned by the watchdog API.
/// </summary>
public sealed record WatchdogState
{
    public DateTime LastCheckAt { get; init; }
    public int SuspectedExchanges { get; init; }
    public int HungExchanges { get; init; }
    public int AutoRestartsPerformed { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<WatchdogAlert> ActiveAlerts { get; init; } = [];
}

/// <summary>
/// A single watchdog alert for a suspected or hung exchange.
/// </summary>
public sealed record WatchdogAlert
{
    public required string ExchangeId { get; init; }
    public required string RouteId { get; init; }
    public required string ContextName { get; init; }

    /// <summary>"Suspected" or "Hung".</summary>
    public required string Level { get; init; }

    public DateTime DetectedAt { get; init; }
    public double ElapsedSeconds { get; init; }
}
