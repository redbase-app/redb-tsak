using System.Diagnostics;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// When this node started: the start time of its process, read once. <c>GET /api/system/info</c> (start time and
/// uptime) and the cluster node registry (<c>StartedAt</c> of the node record) both report this value, so the
/// monitoring page and the topology agree, and neither depends on when it was first asked or on a re-registration.
/// </summary>
public static class NodeStartTime
{
    /// <summary>The start time of this process, in UTC.</summary>
    public static DateTimeOffset Value { get; } = Read();

    private static DateTimeOffset Read()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
