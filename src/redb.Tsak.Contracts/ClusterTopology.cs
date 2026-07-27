namespace redb.Tsak.Contracts;

/// <summary>Snapshot of the entire cluster state from redb EAV.</summary>
public class ClusterTopology
{
    public string ClusterName { get; set; } = "unknown";
    public List<GroupInfo> Groups { get; set; } = [];
    public List<NodeInfo> Nodes { get; set; } = [];
    public LeaderInfo? Leader { get; set; }
    public List<AssignmentInfo> Assignments { get; set; } = [];
}

/// <summary>Cluster group (geographic or logical partition).</summary>
public class GroupInfo
{
    public string GroupName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>Information about a single node in the topology.</summary>
public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public NodeStatus Status { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string ApiEndpoint { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Capabilities { get; set; } = string.Empty;

    /// <summary>Cordoned = takes on no new work (draining/drained). Orthogonal to status.</summary>
    public bool Cordoned { get; set; }

    /// <summary>Node is online and heartbeat is fresh (within 60 seconds).</summary>
    public bool IsAlive => Status == NodeStatus.Online
        && LastHeartbeat > DateTimeOffset.UtcNow.AddSeconds(-60);
}

/// <summary>Current cluster leader information.</summary>
public class LeaderInfo
{
    public string NodeId { get; set; } = string.Empty;
    public long Epoch { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Module-to-node assignment in the cluster.</summary>
public class AssignmentInfo
{
    public string ModuleName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public int State { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public string GroupName { get; set; } = string.Empty;
}
