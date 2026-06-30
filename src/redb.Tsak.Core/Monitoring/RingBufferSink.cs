using Serilog.Core;
using Serilog.Events;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// Serilog sink that feeds structured log events into <see cref="LogRingBuffer"/>.
/// Registered via .WriteTo.Sink() in Program.cs.
/// </summary>
public sealed class RingBufferSink : ILogEventSink
{
    private readonly LogRingBuffer _buffer;

    public RingBufferSink(LogRingBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public void Emit(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
            ? sc.ToString().Trim('"')
            : null;

        _buffer.Add(
            logEvent.Timestamp,
            logEvent.Level.ToShortString(),
            logEvent.RenderMessage(),
            source,
            logEvent.Exception?.ToString());
    }
}

internal static class LogEventLevelExtensions
{
    public static string ToShortString(this LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => level.ToString()
    };
}
