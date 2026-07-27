using System.Text.Json;
using System.Text.Json.Serialization;

namespace redb.Tsak.Core.Monitoring.Alerts;

/// <summary>
/// A single alert to deliver. Produced from a <c>WatchdogAlert</c> (a newly detected hung /
/// suspected exchange) or from a lifecycle event, and carried to every enabled channel.
/// </summary>
public sealed record AlertNotification
{
    /// <summary>Severity: <c>"Suspected"</c> or <c>"Hung"</c> (or <c>"Test"</c> for a manual probe).</summary>
    public required string Level { get; init; }

    /// <summary>Short, human-readable summary line (also the Telegram/webhook title).</summary>
    public required string Title { get; init; }

    public string? ContextName { get; init; }
    public string? RouteId { get; init; }
    public string? ExchangeId { get; init; }
    public double? ElapsedSeconds { get; init; }
    public string? NodeId { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Stable dedup key: the same incident produces the same key so the dispatcher can suppress
    /// repeats within the configured window. Test alerts get a unique key so they always deliver.
    /// </summary>
    [JsonIgnore]
    public string DedupKey => $"{Level}|{ContextName}|{RouteId}|{ExchangeId}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>JSON body sent to the webhook / endpoint channels.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Plain-text rendering for Telegram and email bodies.</summary>
    public string ToText()
    {
        var lines = new List<string> { $"[{Level}] {Title}" };
        if (NodeId is not null) lines.Add($"Node: {NodeId}");
        if (ContextName is not null) lines.Add($"Context: {ContextName}");
        if (RouteId is not null) lines.Add($"Route: {RouteId}");
        if (ExchangeId is not null) lines.Add($"Exchange: {ExchangeId}");
        if (ElapsedSeconds is { } e) lines.Add($"Elapsed: {e:F0}s");
        lines.Add($"At: {Timestamp:u}");
        return string.Join("\n", lines);
    }
}
