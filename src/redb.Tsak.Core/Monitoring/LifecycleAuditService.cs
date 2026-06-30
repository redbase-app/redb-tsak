using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Ring-buffer audit trail for route lifecycle events.
/// Implements <see cref="IRouteLifecycleListener"/> indirectly via per-context wrappers
/// and exposes methods for context-level events and manual recording.
/// </summary>
public sealed class LifecycleAuditService
{
    private readonly ConcurrentQueue<LifecycleEvent> _events = new();
    private readonly ILogger<LifecycleAuditService> _logger;
    private readonly int _maxEvents;

    public LifecycleAuditService(ILogger<LifecycleAuditService> logger, int maxEvents = 1000)
    {
        _logger = logger;
        _maxEvents = maxEvents;
    }

    /// <summary>Total events currently stored.</summary>
    public int Count => _events.Count;

    /// <summary>
    /// Registers the audit trail as a lifecycle listener on a RouteContext.
    /// Creates a per-context wrapper that maps route events to the context name.
    /// </summary>
    public void RegisterContext(string contextName, IRouteContext context)
    {
        if (context is not Route.Core.RouteContext routeCtx)
            return;

        routeCtx.AddLifecycleListener(new ContextLifecycleProxy(this, contextName));
    }

    /// <summary>Records a context-level event.</summary>
    public void RecordContextEvent(string contextName, LifecycleEventType eventType, string? details = null)
    {
        AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, null, eventType, details));
    }

    /// <summary>Records an arbitrary event (e.g., from watchdog).</summary>
    public void Record(LifecycleEvent evt)
    {
        AddEvent(evt);
    }

    /// <summary>
    /// Returns events matching the filters, newest first.
    /// </summary>
    public IReadOnlyList<LifecycleEvent> GetEvents(
        string? contextName = null,
        string? routeId = null,
        LifecycleEventType? eventType = null,
        DateTime? since = null,
        int limit = 100)
    {
        IEnumerable<LifecycleEvent> query = _events.Reverse();

        if (contextName is not null)
            query = query.Where(e => string.Equals(e.ContextName, contextName, StringComparison.OrdinalIgnoreCase));

        if (routeId is not null)
            query = query.Where(e => string.Equals(e.RouteId, routeId, StringComparison.OrdinalIgnoreCase));

        if (eventType.HasValue)
            query = query.Where(e => e.EventType == eventType.Value);

        if (since.HasValue)
            query = query.Where(e => e.Timestamp >= since.Value);

        return query.Take(limit).ToList();
    }

    // ═════════════════════════════════════════════════════════════════
    //  Per-context lifecycle proxy
    // ═════════════════════════════════════════════════════════════════

    private sealed class ContextLifecycleProxy(LifecycleAuditService audit, string contextName)
        : IRouteLifecycleListener
    {
        public Task OnRouteStarted(string routeId, CancellationToken ct)
        {
            audit.AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, routeId,
                LifecycleEventType.RouteStarted, null));
            return Task.CompletedTask;
        }

        public Task OnRouteStopped(string routeId, CancellationToken ct)
        {
            audit.AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, routeId,
                LifecycleEventType.RouteStopped, null));
            return Task.CompletedTask;
        }

        public Task OnRouteSuspending(string routeId, CancellationToken ct)
        {
            audit.AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, routeId,
                LifecycleEventType.RouteSuspended, null));
            return Task.CompletedTask;
        }

        public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
        {
            audit.AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, routeId,
                LifecycleEventType.RouteErrored, ex.Message));
            return Task.CompletedTask;
        }

        public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
        {
            var details = $"Exchange {exchangeId} timed out after {elapsed.TotalSeconds:F0}s";
            audit.AddEvent(new LifecycleEvent(DateTime.UtcNow, contextName, routeId,
                LifecycleEventType.ExchangeTimedOut, details));
            return Task.CompletedTask;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  Private
    // ═════════════════════════════════════════════════════════════════

    private void AddEvent(LifecycleEvent evt)
    {
        _events.Enqueue(evt);

        // Trim ring buffer
        while (_events.Count > _maxEvents)
            _events.TryDequeue(out _);
    }
}
