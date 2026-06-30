namespace redb.Tsak.Contracts;

/// <summary>
/// Logs query response.
/// </summary>
public sealed record LogsResponse
{
    public required bool Available { get; init; }
    public int? Count { get; init; }
    public int? TotalBuffered { get; init; }
    public int? BufferCapacity { get; init; }
    public LogEntry[]? Entries { get; init; }
}

/// <summary>
/// Individual log entry with monotonic ID for incremental fetch.
/// </summary>
public sealed record LogEntry
{
    public required long Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public string? Source { get; init; }
    public string? Exception { get; init; }
}

/// <summary>
/// Log file info for file listing.
/// </summary>
public sealed record LogFileInfo
{
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset LastModified { get; init; }
}

/// <summary>
/// Log files listing response.
/// </summary>
public sealed record LogFilesResponse
{
    public required bool Available { get; init; }
    public LogFileInfo[]? Files { get; init; }
}
