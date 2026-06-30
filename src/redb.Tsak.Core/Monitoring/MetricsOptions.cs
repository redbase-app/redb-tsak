namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Configuration for the metrics collection subsystem.
/// Bound from Tsak:Metrics section.
/// </summary>
public class MetricsOptions
{
    /// <summary>
    /// Interval in seconds between metric collection ticks. Default 10.
    /// </summary>
    public int CollectionIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Hours of history to keep in the circular buffer. Default 12.
    /// Together with CollectionIntervalSeconds determines buffer capacity:
    /// 12h × 3600 / 10 = 4320 points.
    /// </summary>
    public int HistoryHours { get; set; } = 12;

    /// Computed max data points for the circular buffer.
    /// </summary>
    internal int MaxPoints => HistoryHours * 3600 / Math.Max(1, CollectionIntervalSeconds);
}
