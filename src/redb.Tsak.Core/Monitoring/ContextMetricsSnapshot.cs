namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// A snapshot of a single route context's runtime state,
/// including aggregated endpoint statistics from <see cref="redb.Route.Abstractions.IEndpointStatistics"/>.
/// </summary>
public class ContextMetricsSnapshot
{
    public string ContextName { get; set; } = "";
    public bool IsRunning { get; set; }
    public int ComponentCount { get; set; }
    public int EndpointCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public TimeSpan Uptime { get; set; }

    /// <summary>Sum of MessagesIn across all endpoints with statistics.</summary>
    public long MessagesProcessed { get; set; }

    /// <summary>Sum of Errors across all endpoints with statistics.</summary>
    public long ErrorCount { get; set; }

    /// <summary>Most recent LastActivity across all endpoints.</summary>
    public DateTime? LastActivity { get; set; }

    /// <summary>Total in-flight exchanges across all routes.</summary>
    public int InflightCount { get; set; }

    /// <summary>Number of routes that currently have in-flight exchanges.</summary>
    public int RoutesWithInflight { get; set; }
}
