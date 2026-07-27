namespace redb.Tsak.Contracts;

public sealed record ClusterStatusResponse
{
    public required bool Enabled { get; init; }
    public string? NodeId { get; init; }
    public bool? IsLeader { get; init; }
    public long? CurrentEpoch { get; init; }
}

public sealed record ClusterNodesResponse
{
    public required bool Enabled { get; init; }
    public required ClusterNodeInfo[] Nodes { get; init; }
}

public sealed record ClusterNodeInfo
{
    public required string NodeId { get; init; }
    public required string Hostname { get; init; }
    public required int Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastHeartbeat { get; init; }
    public string? ApiEndpoint { get; init; }

    /// <summary>True when the node is cordoned (takes on no new work; draining/drained).</summary>
    public bool Cordoned { get; init; }
}

public sealed record ClusterCordonResponse
{
    public required string NodeId { get; init; }
    public required bool Cordoned { get; init; }
}

public sealed record ClusterRebalanceResponse
{
    public required bool Rebalanced { get; init; }
    public required long CurrentEpoch { get; init; }
}

public sealed record ClusterNodeRemovedResponse
{
    public required string NodeId { get; init; }
    public required bool Removed { get; init; }
}
