using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Modules;
using redb.Tsak.Core.Services;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Adds the reasons for a cold deploy that only a plugin knows: Pro's cluster says whether this node is cordoned.
/// </summary>
public interface IDeployAdviceContributor
{
    /// <summary>Adds codes of <see cref="DeployReasons"/> to <paramref name="reasons"/>.</summary>
    void Contribute(ICollection<string> reasons);
}

/// <summary>
/// The deploy advice of every alive node of the cluster (Pro). Without it the cluster form answers for this node alone.
/// </summary>
public interface IClusterDeployAdvisor
{
    /// <summary>One advice per alive node; <paramref name="self"/> stands for the answering node.</summary>
    Task<IReadOnlyList<DeployAdviceResponse>> AdviseAllAsync(DeployAdviceResponse self, CancellationToken ct = default);
}

/// <summary>
/// Says whether a new package can be hot swapped into this node or the node needs a cold deploy (stop, replace the
/// packages, start). It reads counts only and changes nothing: the deploy stays with the orchestrator.
/// </summary>
public sealed class DeployAdvisor
{
    private readonly ModuleContextMonitor _moduleContexts;
    private readonly HotReloadOptions _options;
    private readonly ITsakContextManager _contexts;
    private readonly HealthCheckService _health;
    private readonly IEnumerable<IDeployAdviceContributor> _contributors;
    private readonly string _nodeId;

    public DeployAdvisor(ModuleContextMonitor moduleContexts, IOptions<HotReloadOptions> options,
        ITsakContextManager contexts, IConfiguration configuration, IEnumerable<IDeployAdviceContributor> contributors,
        HealthCheckService health)
    {
        _moduleContexts = moduleContexts;
        _options = options.Value;
        _contexts = contexts;
        _contributors = contributors;
        _health = health;
        _nodeId = TsakNodeIdentity.Resolve(configuration);
    }

    /// <summary>The advice for this node, taken now.</summary>
    public DeployAdviceResponse Advise() => Advise(Environment.WorkingSet / (1024.0 * 1024));

    internal DeployAdviceResponse Advise(double workingSetMb)
    {
        var orphaned = _moduleContexts.OrphanedModuleContexts;
        var limit = _moduleContexts.MaxOrphanedContexts;
        var reasons = new List<string>();

        if (!StartupProbe.IsFinished(_contexts, _health))
            reasons.Add(DeployReasons.StartupNotFinished);
        if (orphaned > limit)
            reasons.Add(DeployReasons.OrphanedContextsAboveLimit);
        else if (!_options.Collectible && orphaned + 1 > limit)
            reasons.Add(DeployReasons.NextSwapExceedsLimit);
        if (_options.MaxWorkingSetMb is { } maxWorkingSet && workingSetMb > maxWorkingSet)
            reasons.Add(DeployReasons.WorkingSetAboveLimit);
        foreach (var contributor in _contributors)
            contributor.Contribute(reasons);

        return new DeployAdviceResponse
        {
            NodeId = _nodeId,
            Verdict = reasons.Count == 0 ? DeployVerdicts.HotSwap : DeployVerdicts.ColdDeploy,
            Reasons = reasons.Distinct().ToArray(),
            ModuleContextsAlive = _moduleContexts.ModuleContextsAlive,
            OrphanedModuleContexts = orphaned,
            MaxOrphanedModuleContexts = limit,
            WorkingSetMb = Math.Round(workingSetMb, 1),
            MaxWorkingSetMb = _options.MaxWorkingSetMb,
            Collectible = _options.Collectible,
        };
    }
}

/// <summary>Whether the node finished its start: what <c>GET /api/health/startup</c> answers 200 for.</summary>
internal static class StartupProbe
{
    /// <summary>Null when finished, else why not.</summary>
    internal static string? Pending(ITsakContextManager? contexts, HealthCheckService? health)
    {
        if (contexts is null)
            return "Initializing";
        if (contexts.GetAllContexts().Count == 0)
            return "No contexts loaded";
        if (health is not null && health.Evaluate().Checks.TryGetValue("cluster", out var cluster) && cluster != HealthStatus.Healthy)
            return "Cluster coordinator syncing";
        return null;
    }

    internal static bool IsFinished(ITsakContextManager? contexts, HealthCheckService? health) => Pending(contexts, health) is null;
}
