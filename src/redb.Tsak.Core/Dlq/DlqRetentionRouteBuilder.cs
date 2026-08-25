using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Tsak.Core.Dlq;

/// <summary>
/// Daily dead-letter retention as a first-class redb.Route cron route (<c>cron://tsak-dlq-retention</c>)
/// instead of a bare Quartz job — visible in the Routes API/dashboard and the scheduler page, using
/// redb.Route + the Cron connector like any business route. Prunes <c>tsak_dlq</c> entries older than
/// <c>Tsak:Dlq:RetentionDays</c>. The <c>cron://</c> consumer runs on the shared scheduler injected
/// into every Tsak context. Registered by <see cref="Services.SystemContextBuilder"/> only when the DLQ
/// is active and retention is enabled.
/// </summary>
public sealed class DlqRetentionRouteBuilder : RouteBuilder
{
    /// <summary>Route id — how the sweep shows up in the Routes API and the dashboard.</summary>
    public const string RouteIdName = "tsak-dlq-retention";

    /// <summary>Default retention window when the config key is absent.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Daily at 03:05 (Quartz cron) — staggered a few minutes after audit retention.</summary>
    public const string CronSchedule = "0 5 3 * * ?";

    private readonly DlqService _dlq;
    private readonly int _retentionDays;
    private readonly ILogger? _logger;

    public DlqRetentionRouteBuilder(DlqService dlq, int retentionDays, ILogger? logger)
    {
        _dlq = dlq;
        _retentionDays = retentionDays;
        _logger = logger;
    }

    protected override void Configure()
    {
        From($"cron://{RouteIdName}?schedule={CronSchedule}")
            .RouteId(RouteIdName)
            // Cluster-singleton: only the leader runs the daily sweep. In standalone the AlwaysLeader
            // fallback still runs it; in a multi-node cluster it runs once, not once per node. The
            // prune is an idempotent DELETE-by-cutoff, so a run missed during a leader failover is
            // harmless — the next day's sweep covers the same window.
            .Cluster(true)
            .Process(async (IExchange exchange, CancellationToken ct) =>
            {
                if (!_dlq.IsAvailable || _retentionDays <= 0) return;

                var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
                try
                {
                    var removed = await _dlq.PruneAsync(cutoff, ct);
                    if (removed > 0)
                        _logger?.LogInformation(
                            "DLQ retention: removed {Removed} entries older than {Cutoff:o} ({Days}d)",
                            removed, cutoff, _retentionDays);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "DLQ retention sweep failed");
                }
            });
    }
}
