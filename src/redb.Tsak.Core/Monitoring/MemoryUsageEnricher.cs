using Serilog.Core;
using Serilog.Events;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Serilog enricher that adds current GC memory usage (KB) to every log event.
/// Register via: <c>.Enrich.With&lt;MemoryUsageEnricher&gt;()</c>
/// </summary>
public class MemoryUsageEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var memoryKb = GC.GetTotalMemory(false) / 1024;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("MemoryUsage", memoryKb));
    }
}
