using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Models;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Services;

/// <summary>
/// Collects rich context/route/endpoint info from live RouteContext instances.
/// Uses <see cref="IEndpointStatistics"/> for per-endpoint telemetry and
/// <see cref="CompiledRoute"/> for route metadata.
/// Called from heartbeat (every 15s) and on-demand by API controllers.
/// </summary>
public class ContextInfoCollector
{
    private readonly ITsakContextManager _contextManager;

    public ContextInfoCollector(ITsakContextManager contextManager)
    {
        _contextManager = contextManager;
    }

    /// <summary>Collect info for all contexts.</summary>
    public IReadOnlyList<ContextDetail> CollectAll()
    {
        var contexts = _contextManager.GetAllContexts();
        var result = new List<ContextDetail>(contexts.Count);

        foreach (var (name, ctx) in contexts)
            result.Add(BuildContextDetail(name, ctx));

        return result;
    }

    /// <summary>Collect info for a single context.</summary>
    public ContextDetail? Collect(string contextName)
    {
        var ctx = _contextManager.GetContext(contextName);
        return ctx is null ? null : BuildContextDetail(contextName, ctx);
    }

    /// <summary>Collect detailed info for a single route within a context.</summary>
    public RouteDetail? CollectRoute(string contextName, string routeId)
    {
        var ctx = _contextManager.GetContext(contextName) as RouteContext;
        if (ctx is null) return null;

        var route = ctx.GetRoute(routeId);
        if (route is null) return null;

        var inflight = ctx.InflightRepository;
        var endpoints = ctx.GetEndpoints();
        var consumerKey = route.Endpoint.Uri.NormalizedKey;

        // Collect endpoints associated with this route
        var routeEndpoints = endpoints
            .Where(ep => ep.Uri.NormalizedKey.Equals(consumerKey, StringComparison.OrdinalIgnoreCase))
            .Select(ep => BuildEndpointInfo(ep, isConsumer: true))
            .ToArray();

        return new RouteDetail
        {
            RouteId = route.RouteId,
            FromUri = route.Endpoint.Uri.ToMaskedUriString(),
            Status = route.Status.ToString(),
            AutoStart = route.AutoStart,
            InflightCount = inflight.CountByRoute(route.RouteId),
            Endpoints = routeEndpoints,
            PolicyMetadata = route.Policy?.GetMetadata()
        };
    }

    /// <summary>Browse in-flight exchanges for a specific route in a context.</summary>
    public IReadOnlyList<InflightExchangeDto>? BrowseInflight(string contextName, string? routeId = null)
    {
        var ctx = _contextManager.GetContext(contextName) as RouteContext;
        if (ctx is null) return null;

        var entries = routeId is not null
            ? ctx.InflightRepository.Browse(routeId)
            : ctx.InflightRepository.Browse();

        var now = DateTime.UtcNow;
        return entries.Select(e => new InflightExchangeDto
        {
            ExchangeId = e.ExchangeId,
            RouteId = e.RouteId,
            StartedAt = e.StartedAt,
            ElapsedMs = (now - e.StartedAt).TotalMilliseconds,
            ThreadId = e.ManagedThreadId,
            FromEndpoint = e.FromEndpoint
        }).ToArray();
    }

    /// <summary>Collect redb-compatible snapshots for heartbeat propagation. Async so the periodic
    /// heartbeat path never blocks a pool thread on the store-backed auto-start read (review item 3.8).</summary>
    public async Task<Dictionary<string, TsakContextInfo>> CollectSnapshotsAsync()
    {
        var contexts = _contextManager.GetAllContexts();
        var result = new Dictionary<string, TsakContextInfo>(contexts.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, ctx) in contexts)
            result[name] = await BuildSnapshotAsync(name, ctx).ConfigureAwait(false);

        return result;
    }

    private async Task<TsakContextInfo> BuildSnapshotAsync(string name, IRouteContext ctx)
    {
        var endpoints = ctx.GetEndpoints();
        var components = ctx.GetComponentNames();
        var routes = (ctx as RouteContext)?.Routes ?? [];

        var consumerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
            consumerKeys.Add(route.Endpoint.Uri.NormalizedKey);

        var routeSnapshots = new Dictionary<string, TsakRouteSnapshot>(routes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in routes)
        {
            routeSnapshots[r.RouteId] = new TsakRouteSnapshot
            {
                RouteId = r.RouteId,
                FromUri = r.Endpoint.Uri.ToMaskedUriString(),
                Status = r.Status.ToString(),
                AutoStart = r.AutoStart
            };
        }

        var endpointSnapshots = new Dictionary<string, TsakEndpointSnapshot>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var ep in endpoints)
        {
            var isConsumer = consumerKeys.Contains(ep.Uri.NormalizedKey);
            var stats = ep as IEndpointStatistics;

            endpointSnapshots[ep.Uri.NormalizedKey] = new TsakEndpointSnapshot
            {
                Uri = ep.Uri.ToMaskedUriString(),
                Scheme = ep.Uri.Scheme,
                IsConsumer = isConsumer,
                IsProducer = !isConsumer,
                MessagesIn = stats?.MessagesIn ?? 0,
                MessagesOut = stats?.MessagesOut ?? 0,
                Errors = stats?.Errors ?? 0,
                Warnings = stats?.Warnings ?? 0,
                BytesIn = stats?.BytesIn ?? 0,
                ThroughputPerSecond = stats?.ThroughputPerSecond ?? 0,
                HealthStatus = stats?.HealthStatus.ToString() ?? "Healthy",
                HealthReason = stats?.HealthReason,
                LastActivity = stats?.LastActivity is { } la ? new DateTimeOffset(la, TimeSpan.Zero) : null,
                StartedAt = stats != null ? new DateTimeOffset(stats.StartTime, TimeSpan.Zero) : null,
                UptimeSeconds = stats?.Uptime.TotalSeconds ?? 0,
                AverageProcessingTimeMs = stats?.AverageProcessingTime.TotalMilliseconds ?? 0,
                LastErrorMessage = stats?.LastErrorMessage is { Length: > 0 } msg ? msg : null,
                LastErrorTime = stats?.LastErrorTime is { } let ? new DateTimeOffset(let, TimeSpan.Zero) : null,
                LastWarningMessage = stats?.LastWarningMessage is { Length: > 0 } wmsg ? wmsg : null,
                LastWarningTime = stats?.LastWarningTime is { } lwt ? new DateTimeOffset(lwt, TimeSpan.Zero) : null,
            };
        }

        return new TsakContextInfo
        {
            ContextName = name,
            Status = ctx.IsStarted ? "Running" : "Stopped",
            IsAnonymous = name.Contains("_dyn_", StringComparison.Ordinal),
            AutoStart = await _contextManager.ShouldAutoStartAsync(name).ConfigureAwait(false),
            StartedAt = ctx.IsStarted ? DateTimeOffset.UtcNow : null,
            ModuleNames = [], // filled by caller if needed
            Components = components.ToArray(),
            RouteCount = routes.Count,
            EndpointCount = endpoints.Count,
            Routes = routeSnapshots,
            Endpoints = endpointSnapshots
        };
    }

    private ContextDetail BuildContextDetail(string name, IRouteContext ctx)
    {
        var endpoints = ctx.GetEndpoints();
        var components = ctx.GetComponentNames();

        // Get compiled routes (only available via RouteContext)
        var routeCtx = ctx as RouteContext;
        var routes = routeCtx?.Routes ?? [];
        var inflight = routeCtx?.InflightRepository;

        // Build set of consumer endpoint keys for role detection
        var consumerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
            consumerKeys.Add(route.Endpoint.Uri.NormalizedKey);

        var routeInfos = routes.Select(r => new TsakRouteInfo
        {
            RouteId = r.RouteId,
            FromUri = r.Endpoint.Uri.ToMaskedUriString(),
            Status = r.Status.ToString(),
            AutoStart = r.AutoStart,
            InflightCount = inflight?.CountByRoute(r.RouteId) ?? 0,
            IsCluster = r.Policy?.GetMetadata()?.ContainsKey("cluster") == true,
            Policy = new RoutePolicyInfo
            {
                RequestedCluster = r.PolicyDescriptor.RequestedCluster,
                EffectivePolicy = r.PolicyDescriptor.EffectivePolicy,
                PolicyFactoryType = r.PolicyDescriptor.PolicyFactoryType,
                Reason = r.PolicyDescriptor.Reason
            }
        }).ToArray();

        var endpointInfos = endpoints.Select(ep =>
        {
            var isConsumer = consumerKeys.Contains(ep.Uri.NormalizedKey);
            return BuildEndpointInfo(ep, isConsumer);
        }).ToArray();

        return new ContextDetail
        {
            Name = name,
            Status = ctx.IsStarted ? "Running" : "Stopped",
            EndpointCount = endpoints.Count,
            AutoStart = _contextManager.ShouldAutoStart(name),
            IsAnonymous = name.Contains("_dyn_", StringComparison.Ordinal),
            Components = components.ToArray(),
            Routes = routeInfos,
            Endpoints = endpointInfos,
            StartedAt = ctx.IsStarted ? DateTimeOffset.UtcNow : null, // approximate; enriched by heartbeat tracker
        };
    }

    private static TsakEndpointInfo BuildEndpointInfo(IEndpoint ep, bool isConsumer)
    {
        var stats = ep as IEndpointStatistics;

        return new TsakEndpointInfo
        {
            Uri = ep.Uri.ToMaskedUriString(),
            Scheme = ep.Uri.Scheme,
            IsConsumer = isConsumer,
            IsProducer = !isConsumer,
            MessagesIn = stats?.MessagesIn ?? 0,
            MessagesOut = stats?.MessagesOut ?? 0,
            Errors = stats?.Errors ?? 0,
            Warnings = stats?.Warnings ?? 0,
            BytesIn = stats?.BytesIn ?? 0,
            ThroughputPerSecond = stats?.ThroughputPerSecond ?? 0,
            HealthStatus = stats?.HealthStatus.ToString() ?? "Healthy",
            HealthReason = stats?.HealthReason,
            LastActivity = stats?.LastActivity is { } la ? new DateTimeOffset(la, TimeSpan.Zero) : null,
            StartedAt = stats != null ? new DateTimeOffset(stats.StartTime, TimeSpan.Zero) : null,
            Uptime = stats?.Uptime ?? TimeSpan.Zero,
            AverageProcessingTime = stats?.AverageProcessingTime ?? TimeSpan.Zero,
            LastErrorMessage = stats?.LastErrorMessage is { Length: > 0 } msg ? msg : null,
            LastErrorTime = stats?.LastErrorTime is { } let ? new DateTimeOffset(let, TimeSpan.Zero) : null,
            LastWarningMessage = stats?.LastWarningMessage is { Length: > 0 } wmsg ? wmsg : null,
            LastWarningTime = stats?.LastWarningTime is { } lwt ? new DateTimeOffset(lwt, TimeSpan.Zero) : null,
        };
    }

    /// <summary>Builds a per-route metrics summary from the route's consumer endpoint stats.</summary>
    public RouteMetricsSummary? CollectRouteMetrics(string contextName, string routeId)
    {
        var ctx = _contextManager.GetContext(contextName) as RouteContext;
        if (ctx is null) return null;

        var route = ctx.GetRoute(routeId);
        if (route is null) return null;

        return BuildRouteMetrics(ctx, route);
    }

    /// <summary>Collects per-route metrics for all routes across all contexts.</summary>
    public IReadOnlyList<RouteMetricsSummary> CollectAllRouteMetrics()
    {
        var result = new List<RouteMetricsSummary>();
        foreach (var (_, context) in _contextManager.GetAllContexts())
        {
            if (context is not RouteContext ctx) continue;
            foreach (var route in ctx.Routes)
                result.Add(BuildRouteMetrics(ctx, route));
        }
        return result;
    }

    private static RouteMetricsSummary BuildRouteMetrics(RouteContext ctx, CompiledRoute route)
    {
        var stats = route.Endpoint as IEndpointStatistics;
        return new RouteMetricsSummary
        {
            RouteId = route.RouteId,
            Status = route.Status.ToString(),
            InflightCount = ctx.InflightRepository.CountByRoute(route.RouteId),
            MessagesProcessed = stats?.MessagesIn ?? 0,
            Errors = stats?.Errors ?? 0,
            Warnings = stats?.Warnings ?? 0,
            AvgProcessingMs = stats?.AverageProcessingTime.TotalMilliseconds ?? 0,
            ThroughputPerSecond = stats?.ThroughputPerSecond ?? 0,
            LastActivity = stats?.LastActivity,
            LastError = stats?.LastErrorMessage is { Length: > 0 } msg ? msg : null,
            LastErrorTime = stats?.LastErrorTime,
            LastWarning = stats?.LastWarningMessage is { Length: > 0 } wmsg ? wmsg : null,
            LastWarningTime = stats?.LastWarningTime,
            Uptime = stats?.Uptime is { } u && u > TimeSpan.Zero
                ? FormatUptime(u)
                : null
        };
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }
}
