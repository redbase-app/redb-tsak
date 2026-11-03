namespace redb.Tsak.Contracts;

/// <summary>
/// How to deploy a new package on a node, from <c>GET /api/health/deploy</c>: the status code says it alone (200
/// <see cref="DeployVerdicts.HotSwap"/>, 409 <see cref="DeployVerdicts.ColdDeploy"/>). The endpoint only advises; the
/// orchestrator deploys.
/// </summary>
public sealed record DeployAdviceResponse
{
    /// <summary>The node that answers: the cluster node id, or the machine name outside a cluster.</summary>
    public required string NodeId { get; init; }

    /// <summary><see cref="DeployVerdicts.HotSwap"/> or <see cref="DeployVerdicts.ColdDeploy"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>Why a cold deploy, as codes of <see cref="DeployReasons"/>; empty for a hot swap.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Module load contexts in memory.</summary>
    public int ModuleContextsAlive { get; init; }

    /// <summary>Module load contexts in memory after their modules were replaced or removed.</summary>
    public int OrphanedModuleContexts { get; init; }

    /// <summary>The limit of <see cref="OrphanedModuleContexts"/> (<c>Tsak:HotReload:MaxOrphanedContexts</c>).</summary>
    public int MaxOrphanedModuleContexts { get; init; }

    /// <summary>The working set of the process, in MB.</summary>
    public double WorkingSetMb { get; init; }

    /// <summary>The limit of <see cref="WorkingSetMb"/> (<c>Tsak:HotReload:MaxWorkingSetMb</c>); null when none is set.</summary>
    public double? MaxWorkingSetMb { get; init; }

    /// <summary>Whether replaced module versions are unloaded (<c>Tsak:HotReload:Collectible</c>).</summary>
    public bool Collectible { get; init; }

    /// <summary>When the advice was taken.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// The same advice for every alive node of the cluster, from <c>GET /api/health/deploy/cluster</c> on any node: it asks
/// each node of the registry at its API endpoint. 200 only when every node says <see cref="DeployVerdicts.HotSwap"/>,
/// 409 otherwise. Outside a cluster the list holds the answering node alone.
/// </summary>
public sealed record ClusterDeployAdviceResponse
{
    /// <summary><see cref="DeployVerdicts.HotSwap"/> when every node says so, else <see cref="DeployVerdicts.ColdDeploy"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>The node that answered the call.</summary>
    public required string AnsweredBy { get; init; }

    /// <summary>One advice per alive node.</summary>
    public IReadOnlyList<DeployAdviceResponse> Nodes { get; init; } = [];
}

/// <summary>The two verdicts of a deploy advice.</summary>
public static class DeployVerdicts
{
    /// <summary>Drop the package into <c>modules/</c> of the running node.</summary>
    public const string HotSwap = "hot-swap";

    /// <summary>Cordon the node, stop it, replace the packages in <c>modules/</c>, start it, wait for ready, uncordon.</summary>
    public const string ColdDeploy = "cold-deploy";
}

/// <summary>Why a deploy advice says <see cref="DeployVerdicts.ColdDeploy"/>.</summary>
public static class DeployReasons
{
    /// <summary>More module contexts stay in memory than <c>Tsak:HotReload:MaxOrphanedContexts</c> allows.</summary>
    public const string OrphanedContextsAboveLimit = "orphaned-contexts-above-limit";

    /// <summary>With <c>Collectible=false</c> the next hot swap leaves one more context and goes above the limit.</summary>
    public const string NextSwapExceedsLimit = "next-swap-exceeds-limit";

    /// <summary>The working set is above <c>Tsak:HotReload:MaxWorkingSetMb</c>.</summary>
    public const string WorkingSetAboveLimit = "working-set-above-limit";

    /// <summary>The node is cordoned: it is in maintenance.</summary>
    public const string NodeCordoned = "node-cordoned";

    /// <summary>The node has not finished its start (the startup probe would answer 503).</summary>
    public const string StartupNotFinished = "startup-not-finished";

    /// <summary>Cluster form: the node is alive in the registry, but its advice could not be read from its API endpoint.</summary>
    public const string NodeUnreachable = "node-unreachable";
}
