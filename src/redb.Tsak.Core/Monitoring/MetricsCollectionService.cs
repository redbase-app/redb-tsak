using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Background service that periodically collects process metrics
/// and stores them in <see cref="MetricsService"/>.
/// </summary>
public class MetricsCollectionService : BackgroundService
{
    private readonly MetricsService _metricsService;
    private readonly ILogger<MetricsCollectionService> _logger;
    private readonly int _intervalSeconds;

    public MetricsCollectionService(
        MetricsService metricsService,
        ILogger<MetricsCollectionService> logger,
        IOptions<MetricsOptions> options)
    {
        _metricsService = metricsService;
        _logger = logger;
        _intervalSeconds = options.Value.CollectionIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metrics collection started, interval: {Interval}s", _intervalSeconds);

        // Small delay to let the app initialize
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var point = _metricsService.CollectCurrentMetrics();
                _metricsService.AddPoint(point);

                // Log every ~60 seconds
                var stored = _metricsService.StoredPointsCount;
                if (stored % Math.Max(1, 60 / _intervalSeconds) == 0)
                {
                    _logger.LogDebug("Metrics: {Count} points stored, memory: {MemoryMB:F1} MB",
                        stored, point.Memory.WorkingSetMB);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting metrics");
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Metrics collection stopped");
    }
}
