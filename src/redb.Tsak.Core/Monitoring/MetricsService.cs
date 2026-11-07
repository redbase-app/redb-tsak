using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// In-memory metrics store with a circular buffer.
/// Provides collection of process/system metrics and history queries.
/// Cross-platform: Windows, Linux, macOS.
/// </summary>
public class MetricsService
{
    private readonly ConcurrentQueue<MetricsPoint> _buffer = new();
    private readonly object _lock = new();
    private readonly int _maxPoints;

    // Process CPU tracking (static for delta calculation between calls)
    private DateTime _lastCpuCheck = DateTime.UtcNow;
    private TimeSpan _lastProcessCpuTime = TimeSpan.Zero;
    private double _lastProcessCpuUsage;

    // System CPU tracking (delta between calls)
    private DateTime _lastSysCpuCheck = DateTime.UtcNow;
    private long _lastSysCpuTotalTicks;
    private long _lastSysCpuIdleTicks;
    private double _lastSysCpuUsage;

    public MetricsService(IOptions<MetricsOptions> options)
    {
        _maxPoints = options.Value.MaxPoints;
    }

    /// <summary>
    /// Add a metrics data point to the circular buffer.
    /// Evicts oldest entries when buffer is full.
    /// </summary>
    public void AddPoint(MetricsPoint point)
    {
        lock (_lock)
        {
            _buffer.Enqueue(point);
            while (_buffer.Count > _maxPoints)
                _buffer.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Get historical metric data for the given time window.
    /// </summary>
    public IReadOnlyList<MetricsPoint> GetHistory(int minutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
        lock (_lock)
        {
            return _buffer
                .Where(p => p.Timestamp >= cutoff)
                .OrderBy(p => p.Timestamp)
                .ToList();
        }
    }

    /// <summary>
    /// Get the latest (most recent) metrics point, or null if empty.
    /// </summary>
    public MetricsPoint? GetLatest()
    {
        lock (_lock)
        {
            return _buffer.LastOrDefault();
        }
    }

    /// <summary>
    /// Get a full snapshot: latest point + buffer size.
    /// </summary>
    public (MetricsPoint? Latest, int StoredPoints) GetSnapshot()
    {
        lock (_lock)
        {
            return (_buffer.LastOrDefault(), _buffer.Count);
        }
    }

    /// <summary>
    /// Number of data points currently stored.
    /// </summary>
    public int StoredPointsCount => _buffer.Count;

    /// <summary>
    /// Collect current process and system metrics.
    /// </summary>
    public MetricsPoint CollectCurrentMetrics()
    {
        var process = Process.GetCurrentProcess();
        var timestamp = DateTime.UtcNow;

        return new MetricsPoint
        {
            Timestamp = timestamp,
            TimestampMs = ((DateTimeOffset)timestamp).ToUnixTimeMilliseconds(),
            Cpu = new CpuMetrics
            {
                ProcessUsage = GetProcessCpuUsage(process),
                SystemUsage = GetSystemCpuUsage()
            },
            Memory = new MemoryMetrics
            {
                WorkingSetMB = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
                PrivateMemoryMB = Math.Round(process.PrivateMemorySize64 / 1024.0 / 1024.0, 1),
                VirtualMemoryMB = Math.Round(process.VirtualMemorySize64 / 1024.0 / 1024.0, 1),
                GCMemoryMB = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
                SystemMemoryUsageMB = GetSystemMemoryUsage()
            },
            Threading = new ThreadingMetrics
            {
                ActiveThreads = process.Threads.Count,
                ThreadPoolPendingWork = ThreadPool.PendingWorkItemCount
            },
            GarbageCollector = new GcMetrics
            {
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                TotalMemoryMB = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1)
            }
        };
    }

    #region CPU measurement

    private double GetProcessCpuUsage(Process process)
    {
        try
        {
            var now = DateTime.UtcNow;
            var currentCpuTime = process.TotalProcessorTime;

            if (_lastProcessCpuTime == TimeSpan.Zero)
            {
                _lastCpuCheck = now;
                _lastProcessCpuTime = currentCpuTime;
                return 0;
            }

            var timeDiff = now - _lastCpuCheck;
            var cpuDiff = currentCpuTime - _lastProcessCpuTime;

            if (timeDiff.TotalMilliseconds > 0)
            {
                var usage = cpuDiff.TotalMilliseconds / timeDiff.TotalMilliseconds * 100
                            / Environment.ProcessorCount;
                _lastProcessCpuUsage = Math.Round(Math.Clamp(usage, 0, 100), 1);
            }

            _lastCpuCheck = now;
            _lastProcessCpuTime = currentCpuTime;
            return _lastProcessCpuUsage;
        }
        catch
        {
            return 0;
        }
    }

    private double GetSystemCpuUsage()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return GetLinuxSystemCpu();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return GetWindowsSystemCpu();
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private double GetLinuxSystemCpu()
    {
        var lines = File.ReadAllLines("/proc/stat");
        var cpuLine = lines.FirstOrDefault(l => l.StartsWith("cpu "));
        if (cpuLine is null) return 0;

        var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) return 0;

        var idle = long.Parse(parts[4]) + long.Parse(parts[5]);
        var total = parts.Skip(1).Take(7).Sum(long.Parse);

        var deltaTotal = total - _lastSysCpuTotalTicks;
        var deltaIdle = idle - _lastSysCpuIdleTicks;

        _lastSysCpuTotalTicks = total;
        _lastSysCpuIdleTicks = idle;

        if (deltaTotal <= 0) return _lastSysCpuUsage;

        _lastSysCpuUsage = Math.Round(Math.Clamp((double)(deltaTotal - deltaIdle) / deltaTotal * 100, 0, 100), 1);
        return _lastSysCpuUsage;
    }

    private double GetWindowsSystemCpu()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSysCpuCheck).TotalMilliseconds;
        _lastSysCpuCheck = now;

        if (elapsed < 100) return _lastSysCpuUsage;

        var processes = Process.GetProcesses();
        try
        {
            long totalTicks = 0;
            foreach (var p in processes)
            {
                try { totalTicks += p.TotalProcessorTime.Ticks; }
                catch { /* access denied for some system processes */ }
            }

            var deltaTicks = totalTicks - _lastSysCpuTotalTicks;
            _lastSysCpuTotalTicks = totalTicks;

            if (deltaTicks <= 0) return _lastSysCpuUsage;

            // Convert delta ticks to percentage:
            // deltaTicks is CPU time consumed across all cores during elapsed interval
            var elapsedTicks = (long)(elapsed * TimeSpan.TicksPerMillisecond);
            var totalCapacity = elapsedTicks * Environment.ProcessorCount;

            _lastSysCpuUsage = totalCapacity > 0
                ? Math.Round(Math.Clamp((double)deltaTicks / totalCapacity * 100, 0, 100), 1)
                : 0;
            return _lastSysCpuUsage;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    #endregion

    #region Memory measurement

    private static double GetSystemMemoryUsage()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return GetLinuxSystemMemory();
            // Windows/macOS: fallback to managed memory
            return Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1);
        }
        catch
        {
            return 0;
        }
    }

    private static double GetLinuxSystemMemory()
    {
        var lines = File.ReadAllLines("/proc/meminfo");
        long memTotal = 0, memAvailable = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("MemTotal:"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var total))
                    memTotal = total * 1024; // kB → bytes
            }
            else if (line.StartsWith("MemAvailable:"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var available))
                    memAvailable = available * 1024;
            }
        }

        if (memTotal > 0)
            return Math.Round((memTotal - memAvailable) / 1024.0 / 1024.0, 1);
        return 0;
    }

    #endregion
}
