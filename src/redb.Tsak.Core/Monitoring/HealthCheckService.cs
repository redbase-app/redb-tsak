using redb.Tsak.Core.Contracts;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Result of a health check evaluation (internal mutable model).
/// </summary>
public class HealthEvaluation
{
    public HealthStatus Status { get; set; }
    public string Description { get; set; } = "";
    public Dictionary<string, HealthStatus> Checks { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Evaluates overall system health based on context state and pluggable <see cref="IHealthContributor"/> checks.
/// </summary>
public class HealthCheckService
{
    private readonly ITsakContextManager _contextManager;
    private readonly MetricsService _metricsService;
    private readonly IEnumerable<IHealthContributor> _contributors;
    private readonly IEnumerable<IModuleHealthContributor> _moduleContributors;
    private readonly RouteWatchdogService? _watchdog;

    public HealthCheckService(
        ITsakContextManager contextManager,
        MetricsService metricsService,
        IEnumerable<IHealthContributor> contributors,
        IEnumerable<IModuleHealthContributor> moduleContributors,
        RouteWatchdogService? watchdog = null)
    {
        _contextManager = contextManager;
        _metricsService = metricsService;
        _contributors = contributors;
        _moduleContributors = moduleContributors;
        _watchdog = watchdog;
    }

    /// <summary>
    /// Backward-compatible constructor without module contributors. New code should pass
    /// <see cref="IModuleHealthContributor"/> enumerations explicitly.
    /// </summary>
    public HealthCheckService(
        ITsakContextManager contextManager,
        MetricsService metricsService,
        IEnumerable<IHealthContributor> contributors,
        RouteWatchdogService? watchdog = null)
        : this(contextManager, metricsService, contributors, Array.Empty<IModuleHealthContributor>(), watchdog)
    {
    }

    /// <summary>
    /// Run all health checks and return aggregated result.
    /// <para>
    /// <b>Note:</b> this synchronous overload does NOT execute <see cref="IModuleHealthContributor"/>
    /// probes (which are async by contract). Use <see cref="EvaluateAsync"/> to include them.
    /// </para>
    /// </summary>
    public HealthEvaluation Evaluate()
    {
        var result = new HealthEvaluation();

        CheckContexts(result);
        CheckMetrics(result);

        foreach (var contributor in _contributors)
            contributor.Contribute(result);

        if (_watchdog is not null)
            CheckWatchdog(result);

        // Aggregate: worst status wins
        result.Status = result.Checks.Values.DefaultIfEmpty(HealthStatus.Healthy).Max();

        result.Description = result.Status switch
        {
            HealthStatus.Healthy => "All checks passed",
            HealthStatus.Degraded => "Some checks degraded",
            HealthStatus.Unhealthy => "One or more checks failed",
            _ => "Unknown"
        };

        return result;
    }

    /// <summary>
    /// Async variant of <see cref="Evaluate"/> that additionally awaits all registered
    /// <see cref="IModuleHealthContributor"/> probes and merges them under <c>module:{name}</c>
    /// keys. Probe exceptions are caught and translated to <see cref="HealthStatus.Unhealthy"/>
    /// — a buggy probe never crashes the health endpoint.
    /// </summary>
    public async Task<HealthEvaluation> EvaluateAsync(CancellationToken ct = default)
    {
        var result = Evaluate();
        await RunModuleContributorsAsync(result, ct).ConfigureAwait(false);

        // Re-aggregate after module checks may have added entries.
        result.Status = result.Checks.Values.DefaultIfEmpty(HealthStatus.Healthy).Max();
        result.Description = result.Status switch
        {
            HealthStatus.Healthy => "All checks passed",
            HealthStatus.Degraded => "Some checks degraded",
            HealthStatus.Unhealthy => "One or more checks failed",
            _ => "Unknown"
        };
        return result;
    }

    private async Task RunModuleContributorsAsync(HealthEvaluation result, CancellationToken ct)
    {
        foreach (var contributor in _moduleContributors)
        {
            HealthStatus status;
            try
            {
                status = await contributor.CheckHealthAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Buggy probe → Unhealthy, but don't propagate.
                status = HealthStatus.Unhealthy;
            }

            var key = "module:" + contributor.ModuleName;
            // Tie-break: worst status wins if two contributors share the same module name.
            if (result.Checks.TryGetValue(key, out var existing) && existing > status)
                continue;
            result.Checks[key] = status;
        }
    }

    private void CheckContexts(HealthEvaluation result)
    {
        var contexts = _contextManager.GetAllContexts();

        if (contexts.Count == 0)
        {
            result.Checks["contexts"] = HealthStatus.Degraded;
            return;
        }

        var running = contexts.Count(c => c.Context.IsStarted);
        if (running == contexts.Count)
            result.Checks["contexts"] = HealthStatus.Healthy;
        else if (running > 0)
            result.Checks["contexts"] = HealthStatus.Degraded;
        else
            result.Checks["contexts"] = HealthStatus.Unhealthy;
    }

    private void CheckMetrics(HealthEvaluation result)
    {
        var latest = _metricsService.GetLatest();
        if (latest is null)
        {
            // No metrics collected yet — degraded (just started)
            result.Checks["metrics"] = HealthStatus.Degraded;
            return;
        }

        // Memory pressure check: > 90% of system memory is unhealthy
        if (latest.Memory.WorkingSetMB > 0 && latest.Memory.SystemMemoryUsageMB > 0)
        {
            // This is a rough heuristic; only flags extreme states
            var gcPressure = GC.CollectionCount(2) > 0 &&
                             latest.GarbageCollector.Gen2Collections > 100;
            result.Checks["metrics"] = gcPressure ? HealthStatus.Degraded : HealthStatus.Healthy;
        }
        else
        {
            result.Checks["metrics"] = HealthStatus.Healthy;
        }
    }

    private void CheckWatchdog(HealthEvaluation result)
    {
        if (!_watchdog!.IsEnabled)
        {
            result.Checks["watchdog"] = HealthStatus.Healthy;
            return;
        }

        var state = _watchdog.GetState();

        if (state.HungExchanges > 0)
            result.Checks["watchdog"] = HealthStatus.Unhealthy;
        else if (state.SuspectedExchanges > 0)
            result.Checks["watchdog"] = HealthStatus.Degraded;
        else
            result.Checks["watchdog"] = HealthStatus.Healthy;
    }
}
