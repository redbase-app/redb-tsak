using System.Globalization;

namespace redb.Tsak.Web.Services;

/// <summary>
/// How the dashboard writes the time of a log line: the line's own timestamp in the node's time zone (the offset the
/// worker logged it with), date included, so lines of different days are told apart. The tooltip form adds the offset,
/// as the worker's log files write it. Invariant culture: some cultures replace the ':' separators.
/// </summary>
public static class LogTime
{
    /// <summary>Column header of a log line's time.</summary>
    public const string Header = "Node time";

    /// <summary>"2026-11-09 10:01:04.349", in the node's time zone.</summary>
    public static string Format(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>"2026-11-09 10:01:04.349 +01:00": the same moment with the node's UTC offset.</summary>
    public static string FormatWithOffset(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
}
