using Serilog.Core;
using Serilog.Events;

namespace redb.Tsak.Worker.Utils;

public class MemoryUsageEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var memoryUsage = GC.GetTotalMemory(false) / 1024; // KB
        var property = propertyFactory.CreateProperty("MemoryUsage", memoryUsage);
        logEvent.AddPropertyIfAbsent(property);
    }
}
