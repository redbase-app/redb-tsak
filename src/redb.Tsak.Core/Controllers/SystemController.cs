using System.Reflection;
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
}
