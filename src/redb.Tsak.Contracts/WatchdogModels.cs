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

/// <summary>Alert-delivery status: whether delivery is active and which channels are enabled.</summary>
public sealed record AlertDeliveryStatus
{
    public required bool Active { get; init; }
    public string MinLevel { get; init; } = "";
    public IReadOnlyList<string> Channels { get; init; } = [];
}

/// <summary>Result of <c>POST /api/watchdog/test-alert</c>: per-channel delivery outcome.</summary>
public sealed record AlertTestResult
{
    public required bool Delivered { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<AlertChannelOutcome> Channels { get; init; } = [];
}

/// <summary>Outcome of delivering the test alert to one channel.</summary>
public sealed record AlertChannelOutcome
{
    public required string Channel { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
