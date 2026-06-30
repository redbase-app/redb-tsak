using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using redb.Route.Core;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Background service that periodically scans all route contexts for
/// suspected (long-running) and hung (stale) in-flight exchanges.
/// Emits log warnings/errors and maintains a queryable alert state.
/// </summary>
public sealed class RouteWatchdogService : BackgroundService
{
    private readonly ITsakContextManager _contextManager;
    private readonly ILogger<RouteWatchdogService> _logger;
    private readonly WatchdogOptions _options;

    private readonly object _lock = new();
    private volatile bool _enabled;
    private DateTime _lastCheckAt;
    private int _autoRestartsPerformed;
    private readonly List<WatchdogAlert> _activeAlerts = [];

    public RouteWatchdogService(
        ITsakContextManager contextManager,
        ILogger<RouteWatchdogService> logger,
        IOptions<WatchdogOptions> options)
    {
        _contextManager = contextManager;
        _logger = logger;
        _options = options.Value;
        _enabled = _options.Enabled;
    }

    /// <summary>Enables the watchdog at runtime.</summary>
    public void Enable()
    {
        _enabled = true;
        _logger.LogInformation("Route watchdog enabled");
    }

    /// <summary>Disables the watchdog at runtime.</summary>
    public void Disable()
    {
        _enabled = false;
        lock (_lock) _activeAlerts.Clear();
        _logger.LogInformation("Route watchdog disabled");
    }

    /// <summary>Returns true if the watchdog is currently enabled.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Returns a snapshot of the current watchdog state.</summary>
    public WatchdogState GetState()
    {
        lock (_lock)
        {
            return new WatchdogState
            {
                LastCheckAt = _lastCheckAt,
                SuspectedExchanges = _activeAlerts.Count(a => a.Level == "Suspected"),
                HungExchanges = _activeAlerts.Count(a => a.Level == "Hung"),
                AutoRestartsPerformed = _autoRestartsPerformed,
                Enabled = _enabled,
                ActiveAlerts = _activeAlerts.ToList()
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Route watchdog started, interval: {Interval}s, suspected: {Suspected}m, hung: {Hung}m",
            _options.CheckIntervalSeconds,
            _options.SuspectedThresholdMinutes,
            _options.HungThresholdMinutes);

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_enabled)
            {
                try
                {
                    await ScanAllContextsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during watchdog scan");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Route watchdog stopped");
    }

    private async Task ScanAllContextsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var suspectedThreshold = TimeSpan.FromMinutes(_options.SuspectedThresholdMinutes);
        var hungThreshold = TimeSpan.FromMinutes(_options.HungThresholdMinutes);
        var newAlerts = new List<WatchdogAlert>();

        foreach (var (contextName, context) in _contextManager.GetAllContexts())
        {
            if (context is not RouteContext routeCtx)
                continue;

            var inflights = routeCtx.InflightRepository.Browse();

            foreach (var inflight in inflights)
            {
                var elapsed = now - inflight.StartedAt;

                if (elapsed >= hungThreshold)
                {
                    _logger.LogError(
                        "Hung exchange {ExchangeId} in route {RouteId} (context: {Context}), elapsed: {Elapsed:F0}s",
                        inflight.ExchangeId, inflight.RouteId, contextName, elapsed.TotalSeconds);

                    newAlerts.Add(new WatchdogAlert
                    {
                        ExchangeId = inflight.ExchangeId,
                        RouteId = inflight.RouteId,
                        ContextName = contextName,
                        Level = "Hung",
                        DetectedAt = now,
                        ElapsedSeconds = elapsed.TotalSeconds
                    });

                    if (_options.AutoRestartHungRoutes && !ct.IsCancellationRequested)
                    {
                        await TryAutoRestartRouteAsync(contextName, inflight.RouteId, ct);
                    }
                }
                else if (elapsed >= suspectedThreshold)
                {
                    _logger.LogWarning(
                        "Suspected exchange {ExchangeId} in route {RouteId} (context: {Context}), elapsed: {Elapsed:F0}s",
                        inflight.ExchangeId, inflight.RouteId, contextName, elapsed.TotalSeconds);

                    newAlerts.Add(new WatchdogAlert
                    {
                        ExchangeId = inflight.ExchangeId,
                        RouteId = inflight.RouteId,
                        ContextName = contextName,
                        Level = "Suspected",
                        DetectedAt = now,
                        ElapsedSeconds = elapsed.TotalSeconds
                    });
                }
            }
        }

        lock (_lock)
        {
            _activeAlerts.Clear();
            _activeAlerts.AddRange(newAlerts);
            _lastCheckAt = now;
        }
    }

    private async Task TryAutoRestartRouteAsync(string contextName, string routeId, CancellationToken ct)
    {
        try
        {
            _logger.LogWarning("Auto-restarting context {Context} due to hung route {RouteId}",
                contextName, routeId);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.AutoRestartTimeoutSeconds));

            await _contextManager.RestartContextAsync(contextName, cts.Token);
            Interlocked.Increment(ref _autoRestartsPerformed);

            _logger.LogInformation("Auto-restart completed for context {Context}", contextName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-restart failed for context {Context}", contextName);
        }
    }
}
