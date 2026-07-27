using System.Reflection;
using Microsoft.Extensions.Configuration;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Core.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;
using Dto = redb.Tsak.Contracts;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// System-level endpoints: health, metrics, info.
/// GET /api/system/health   — health check
/// GET /api/system/metrics  — latest metrics snapshot
/// GET /api/system/info     — version, uptime, etc.
/// </summary>
[Route("/api/system")]
public class SystemController : RedbController
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    [HttpGet("/health")]
    public async Task<object> GetHealth()
    {
        var healthSvc = Context.GetService<HealthCheckService>();
        if (healthSvc is null)
            return new Dto.HealthCheckResult
            {
                Status = Dto.HealthStatus.Unhealthy,
                Description = "HealthCheckService not available.",
                Checks = new Dictionary<string, Dto.HealthStatus>(),
                Timestamp = DateTime.UtcNow
            };

        var eval = await healthSvc.EvaluateAsync();
        return new Dto.HealthCheckResult
        {
            Status = eval.Status,
            Description = eval.Description,
            Checks = eval.Checks,
            Timestamp = eval.Timestamp
        };
    }

    [HttpGet("/metrics")]
    public object GetMetrics()
    {
        var metricsSvc = Context.GetService<MetricsService>();
        if (metricsSvc is null)
            return new Dto.MetricsResponse { Available = false };

        var (latest, storedPoints) = metricsSvc.GetSnapshot();
        return new Dto.MetricsResponse
        {
            Available = true,
            StoredPoints = storedPoints,
            Latest = latest is null ? null : new Dto.MetricSnapshot
            {
                Timestamp = latest.Timestamp,
                TimestampMs = latest.TimestampMs,
                Cpu = new Dto.CpuMetrics
                {
                    ProcessUsage = latest.Cpu.ProcessUsage,
                    SystemUsage = latest.Cpu.SystemUsage
                },
                Memory = new Dto.MemoryMetrics
                {
                    WorkingSetMB = latest.Memory.WorkingSetMB,
                    PrivateMemoryMB = latest.Memory.PrivateMemoryMB,
                    VirtualMemoryMB = latest.Memory.VirtualMemoryMB,
                    GCMemoryMB = latest.Memory.GCMemoryMB,
                    SystemMemoryUsageMB = latest.Memory.SystemMemoryUsageMB
                },
                Threading = new Dto.ThreadingMetrics
                {
                    ActiveThreads = latest.Threading.ActiveThreads,
                    ThreadPoolPendingWork = latest.Threading.ThreadPoolPendingWork
                },
                GarbageCollector = new Dto.GcMetrics
                {
                    Gen0Collections = latest.GarbageCollector.Gen0Collections,
                    Gen1Collections = latest.GarbageCollector.Gen1Collections,
                    Gen2Collections = latest.GarbageCollector.Gen2Collections,
                    TotalMemoryMB = latest.GarbageCollector.TotalMemoryMB
                }
            }
        };
    }

    [HttpGet("/metrics/history")]
    public object GetMetricsHistory([FromQuery("minutes")] int? minutesParam)
    {
        var metricsSvc = Context.GetService<MetricsService>();
        if (metricsSvc is null)
            return new Dto.MetricsHistoryResponse { Available = false };

        var minutes = minutesParam ?? 5;
        if (minutes < 1) minutes = 1;
        if (minutes > 720) minutes = 720;

        var history = metricsSvc.GetHistory(minutes);
        return new Dto.MetricsHistoryResponse
        {
            Available = true,
            Points = history.Select(p => new Dto.MetricSnapshot
            {
                Timestamp = p.Timestamp,
                TimestampMs = p.TimestampMs,
                Cpu = new Dto.CpuMetrics { ProcessUsage = p.Cpu.ProcessUsage, SystemUsage = p.Cpu.SystemUsage },
                Memory = new Dto.MemoryMetrics
                {
                    WorkingSetMB = p.Memory.WorkingSetMB,
                    PrivateMemoryMB = p.Memory.PrivateMemoryMB,
                    VirtualMemoryMB = p.Memory.VirtualMemoryMB,
                    GCMemoryMB = p.Memory.GCMemoryMB,
                    SystemMemoryUsageMB = p.Memory.SystemMemoryUsageMB
                },
                Threading = new Dto.ThreadingMetrics
                {
                    ActiveThreads = p.Threading.ActiveThreads,
                    ThreadPoolPendingWork = p.Threading.ThreadPoolPendingWork
                },
                GarbageCollector = new Dto.GcMetrics
                {
                    Gen0Collections = p.GarbageCollector.Gen0Collections,
                    Gen1Collections = p.GarbageCollector.Gen1Collections,
                    Gen2Collections = p.GarbageCollector.Gen2Collections,
                    TotalMemoryMB = p.GarbageCollector.TotalMemoryMB
                }
            }).ToArray()
        };
    }

    [HttpGet("/info")]
    public object GetInfo()
    {
        var contextMgr = Context.GetService<ITsakContextManager>();
        var moduleReg = Context.GetService<ITsakModuleRegistry>();

        return new Dto.SystemInfoResponse
        {
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            StartedAt = StartTime.UtcDateTime,
            Uptime = (DateTimeOffset.UtcNow - StartTime).ToString(),
            ContextCount = contextMgr?.GetAllContexts().Count ?? 0,
            ModuleCount = moduleReg?.GetAllModules().Count ?? 0,
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetMb = Environment.WorkingSet / (1024.0 * 1024)
        };
    }

    /// <summary>
    /// Effective (merged, resolved) configuration for this node — the <c>Tsak:*</c> and
    /// <c>ConnectionStrings:*</c> settings the process is actually running with, flattened and with
    /// secrets redacted. Answers "what config is this node on" without SSH-ing in. Admin-only: it
    /// reveals topology (providers, connection hosts, feature flags).
    /// </summary>
    [HttpGet("/config")]
    [RequiresRole(TsakRoles.Admin)]
    public object GetConfig()
    {
        var config = Context.GetService<IConfiguration>();
        if (config is null)
            return new Dto.EffectiveConfigResult { Available = false };

        var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var redacted = 0;

        foreach (var section in new[] { "Tsak", "ConnectionStrings" })
        {
            foreach (var kv in config.GetSection(section).AsEnumerable(makePathsRelative: false))
            {
                if (kv.Value is null) continue; // structural node, not a leaf value
                var (value, wasRedacted) = ConfigRedactor.Redact(kv.Key, kv.Value);
                values[kv.Key] = value;
                if (wasRedacted) redacted++;
            }
        }

        return new Dto.EffectiveConfigResult { Available = true, RedactedCount = redacted, Values = values };
    }

    /// <summary>
    /// The assemblies actually loaded in the process — name, version, and where each came from
    /// (shared layer / app bin / .NET runtime). Since the redb.* framework is byte-loaded from
    /// Libs/shared its <c>Location</c> is empty, so shared origin is inferred from the shared-layer
    /// tracker. Answers "which redb is REALLY running" on an incident without SSH-ing in — the
    /// diagnostic counterpart to the swappable shared layer. Admin-only.
    /// </summary>
    [HttpGet("/assemblies")]
    [RequiresRole(TsakRoles.Admin)]
    public object GetAssemblies()
    {
        var baseDir = AppContext.BaseDirectory;
        var items = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a =>
            {
                var name = a.GetName();
                string location = "";
                try { location = a.Location; } catch { /* byte-loaded → not supported */ }

                string source;
                if (Modules.SharedRuntime.IsLoaded(name.Name ?? "") && string.IsNullOrEmpty(location))
                    source = "shared";                 // byte-loaded from Libs/shared (no Location)
                else if (string.IsNullOrEmpty(location))
                    source = "in-memory";
                else if (location.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    source = location.Contains($"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}",
                                 StringComparison.OrdinalIgnoreCase) ? "shared" : "bin";
                else
                    source = "runtime";

                return new
                {
                    Name = name.Name,
                    Version = name.Version?.ToString(),
                    Source = source,
                    Location = location
                };
            })
            .Where(x => x.Name is not null)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var redb = items.Where(x => x.Name!.StartsWith("redb.", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
        return new
        {
            Count = items.Length,
            RedbCount = redb.Length,
            Redb = redb,          // the interesting ones first, at a glance
            All = items
        };
    }
}
