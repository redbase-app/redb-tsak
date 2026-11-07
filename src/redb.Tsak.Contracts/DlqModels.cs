namespace redb.Tsak.Contracts;

/// <summary>
/// Response for <c>GET /api/exchanges/failed</c>: a newest-first page of dead-lettered exchanges.
/// <see cref="Available"/> is <c>false</c> when the node runs without a database (the DLQ needs a
/// durable store).
/// </summary>
public sealed record FailedExchangeQueryResult
{
    public required bool Available { get; init; }
    public int? Count { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public string? Error { get; init; }
    public FailedExchangeEntry[] Entries { get; init; } = [];
}

/// <summary>A single dead-lettered exchange captured at a route checkpoint.</summary>
public sealed record FailedExchangeEntry
{
    public required string EntryId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string ContextName { get; init; }
    public required string RouteId { get; init; }
    public required string MarkerName { get; init; }
    public string Status { get; init; } = "pending";
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Body kind: <c>bytes</c> / <c>string</c> / <c>json</c> / <c>none</c>.</summary>
    public string BodyKind { get; init; } = "none";

    /// <summary>Whether this entry can be replayed after a restart (body/state round-trips).</summary>
    public bool Replayable { get; init; }

    public DateTimeOffset? ReplayedAt { get; init; }
}

/// <summary>Result of <c>POST /api/exchanges/{id}/replay</c>.</summary>
public sealed record ExchangeReplayResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? EntryId { get; init; }
}
