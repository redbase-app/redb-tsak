using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Tsak.Core.Audit;

/// <summary>
/// Daily audit-log retention as a first-class redb.Route cron route (<c>cron://tsak-audit-retention</c>)
/// instead of a bare Quartz job — so it shows up in the Routes API/dashboard as well as the scheduler
/// page, and Tsak eats its own dog food (uses redb.Route + the Cron connector like any business route).
/// Prunes <c>tsak_audit_log</c> rows older than <c>Tsak:Audit:RetentionDays</c>. The <c>cron://</c>
/// consumer runs on the shared scheduler injected into every Tsak context, so it stays visible on the
/// scheduler dashboard. Registered by <see cref="Services.SystemContextBuilder"/> only when audit
/// persistence is active and retention is enabled.
/// </summary>
public sealed class AuditRetentionRouteBuilder : RouteBuilder
{
    /// <summary>Route id — how the sweep shows up in the Routes API and the dashboard.</summary>
    public const string RouteIdName = "tsak-audit-retention";

    /// <summary>Default retention window when the config key is absent.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>Daily at 03:00 (Quartz cron). Kept off the top of the hour of nothing in particular.</summary>
    public const string CronSchedule = "0 0 3 * * ?";

    private readonly AuditQueryService _audit;
    private readonly int _retentionDays;
    private readonly ILogger? _logger;

    public AuditRetentionRouteBuilder(AuditQueryService audit, int retentionDays, ILogger? logger)
    {
        _audit = audit;
        _retentionDays = retentionDays;
        _logger = logger;
    }

    protected override void Configure()
    {
        From($"cron://{RouteIdName}?schedule={CronSchedule}")
            .RouteId(RouteIdName)
            .Process(async (IExchange exchange, CancellationToken ct) =>
            {
                if (!_audit.IsAvailable || _retentionDays <= 0) return;

                var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
                try
                {
                    var removed = await _audit.PruneAsync(cutoff, ct);
                    if (removed > 0)
                        _logger?.LogInformation(
                            "Audit retention: removed {Removed} entries older than {Cutoff:o} ({Days}d)",
                            removed, cutoff, _retentionDays);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Audit retention sweep failed");
                }
            });
    }
}
