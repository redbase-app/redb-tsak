using System.Text.Json.Serialization;

namespace redb.Tsak.Contracts;

/// <summary>
/// A single lifecycle event recorded by the audit trail.
/// </summary>
public sealed record LifecycleEvent(
    DateTime Timestamp,
    string ContextName,
    string? RouteId,
    LifecycleEventType EventType,
    string? Details);

/// <summary>
/// Types of lifecycle events tracked by the audit trail.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LifecycleEventType
{
    ContextStarted,
    ContextStopped,
    ContextRemoved,
    RouteStarted,
    RouteStopped,
    RouteSuspended,
    RouteErrored,
    ExchangeTimedOut,
    ExchangeHung,
    WatchdogAutoRestart
}
