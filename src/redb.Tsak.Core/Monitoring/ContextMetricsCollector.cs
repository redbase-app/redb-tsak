using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Collects per-context metrics from the context manager.
/// Aggregates endpoint statistics via <see cref="IEndpointStatistics"/>.
/// </summary>
public class ContextMetricsCollector
{
    private readonly ITsakContextManager _contextManager;

    // Track start times for uptime calculation
    private readonly Dictionary<string, DateTime> _startTimes = new();
    private readonly object _lock = new();

    public ContextMetricsCollector(ITsakContextManager contextManager)
    {
        _contextManager = contextManager;
    }

    /// <summary>
    /// Notify that a context was started (call from coordinator/hosted service).
    /// </summary>
    public void TrackStarted(string contextName)
    {
        lock (_lock)
        {
            _startTimes[contextName] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Notify that a context was stopped.
    /// </summary>
    public void TrackStopped(string contextName)
    {
        lock (_lock)
        {
            _startTimes.Remove(contextName);
        }
    }

    /// <summary>
    /// Collect current metrics for all known contexts.
    /// </summary>
    public IReadOnlyList<ContextMetricsSnapshot> CollectAll()
    {
        var contexts = _contextManager.GetAllContexts();
        var result = new List<ContextMetricsSnapshot>(contexts.Count);

        foreach (var (name, ctx) in contexts)
            result.Add(BuildSnapshot(name, ctx));

        return result;
    }

    /// <summary>
    /// Collect metrics for a single context by name.
    /// </summary>
    public ContextMetricsSnapshot? Collect(string contextName)
    {
        var ctx = _contextManager.GetContext(contextName);
        return ctx is null ? null : BuildSnapshot(contextName, ctx);
    }

    private ContextMetricsSnapshot BuildSnapshot(string name, IRouteContext ctx)
    {
        DateTime? startedAt;
        lock (_lock)
        {
            _startTimes.TryGetValue(name, out var st);
            startedAt = st == default ? null : st;
        }

        var now = DateTime.UtcNow;
        long totalMessages = 0;
        long totalErrors = 0;
        DateTime? lastActivity = null;
        var endpoints = ctx.GetEndpoints();

        foreach (var ep in endpoints)
        {
            if (ep is IEndpointStatistics stats)
            {
                totalMessages += stats.MessagesIn;
                totalErrors += stats.Errors;

                if (stats.LastActivity is { } la && (lastActivity is null || la > lastActivity))
                    lastActivity = la;
            }
        }

        return new ContextMetricsSnapshot
        {
            ContextName = name,
            IsRunning = ctx.IsStarted,
            ComponentCount = ctx.GetComponentNames().Count,
            EndpointCount = endpoints.Count,
            StartedAt = startedAt,
            Uptime = startedAt.HasValue ? now - startedAt.Value : TimeSpan.Zero,
            MessagesProcessed = totalMessages,
            ErrorCount = totalErrors,
            LastActivity = lastActivity,
            InflightCount = (ctx as RouteContext)?.InflightRepository.Count ?? 0,
            RoutesWithInflight = CountRoutesWithInflight(ctx as RouteContext),
        };
    }

    private static int CountRoutesWithInflight(RouteContext? ctx)
    {
        if (ctx is null) return 0;
        var inflight = ctx.InflightRepository;
        return ctx.Routes.Count(r => inflight.CountByRoute(r.RouteId) > 0);
    }
}
