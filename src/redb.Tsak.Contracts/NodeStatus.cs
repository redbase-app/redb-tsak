namespace redb.Tsak.Contracts;

/// <summary>
/// Status of a node (cluster or standalone).
/// Canonical source — all other code references this enum.
/// </summary>
public enum NodeStatus
{
    /// <summary>Node is running and sending heartbeats.</summary>
    Online,

    /// <summary>Node gracefully shut down.</summary>
    Offline,

    /// <summary>Node missed heartbeat threshold — considered dead.</summary>
    Dead
}
