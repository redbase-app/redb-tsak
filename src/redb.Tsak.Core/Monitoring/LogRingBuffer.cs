using redb.Tsak.Contracts;

namespace redb.Tsak.Core.Monitoring;

/// <summary>
/// In-memory circular buffer for recent log entries.
/// Each entry gets a monotonic ID for incremental fetch.
/// Thread-safe via lock. Capacity configured by Tsak:Logs:BufferSize.
/// </summary>
public sealed class LogRingBuffer
{
    private readonly LogEntry[] _buffer;
    private int _head;
    private int _count;
    private long _nextId;
    private readonly object _lock = new();

    public int Capacity { get; }

    public LogRingBuffer(int capacity = 2000)
    {
        Capacity = capacity;
        _buffer = new LogEntry[capacity];
    }

    /// <summary>Add entry with auto-assigned ID.</summary>
    public void Add(DateTimeOffset timestamp, string level, string message, string? source, string? exception)
    {
        lock (_lock)
        {
            var entry = new LogEntry
            {
                Id = _nextId++,
                Timestamp = timestamp,
                Level = level,
                Message = message,
                Source = source,
                Exception = exception
            };
            _buffer[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Get entries newer than afterId (oldest first). Supports level filter.</summary>
    public IReadOnlyList<LogEntry> GetAfter(long afterId, int limit = 500, string? levelFilter = null)
    {
        lock (_lock)
        {
            var result = new List<LogEntry>(Math.Min(limit, _count));

            // Walk oldest → newest
            for (int i = _count - 1; i >= 0 && result.Count < limit; i--)
            {
                var idx = (_head - 1 - i + Capacity) % Capacity;
                var entry = _buffer[idx];
                if (entry is null) continue;
                if (entry.Id <= afterId) continue;
                if (levelFilter is not null &&
                    !string.Equals(entry.Level, levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(entry);
            }
            return result;
        }
    }

    /// <summary>Get most recent entries, newest first. Optional limit and level filter.</summary>
    public IReadOnlyList<LogEntry> GetRecent(int limit = 100, string? levelFilter = null)
    {
        lock (_lock)
        {
            var result = new List<LogEntry>(Math.Min(limit, _count));
            for (int i = 0; i < _count && result.Count < limit; i++)
            {
                var idx = (_head - 1 - i + Capacity) % Capacity;
                var entry = _buffer[idx];
                if (entry is null) continue;
                if (levelFilter is not null &&
                    !string.Equals(entry.Level, levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(entry);
            }
            return result;
        }
    }

    public int Count
    {
        get { lock (_lock) return _count; }
    }
}
