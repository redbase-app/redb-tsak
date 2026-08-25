using System.Collections.Concurrent;

namespace redb.Tsak.Web.Security;

/// <summary>
/// Per-login failed-attempt throttle for the dashboard sign-in (review item 2.6). After
/// <see cref="MaxAttempts"/> failures within the window a login is locked out for
/// <see cref="LockoutDuration"/>, defeating online password guessing. A successful login clears the
/// counter. Registered as a singleton so the state is shared across all requests on the node.
/// <para>Pure and clock-injectable so lockout timing is unit-testable without waiting.</para>
/// </summary>
public sealed class LoginThrottle
{
    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public int MaxAttempts { get; }
    public TimeSpan Window { get; }
    public TimeSpan LockoutDuration { get; }

    public LoginThrottle(
        int maxAttempts = 5,
        TimeSpan? window = null,
        TimeSpan? lockoutDuration = null,
        Func<DateTimeOffset>? now = null)
    {
        MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        Window = window ?? TimeSpan.FromMinutes(5);
        LockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(1);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when <paramref name="key"/> is currently locked out.</summary>
    public bool IsLockedOut(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!_entries.TryGetValue(key, out var e)) return false;
        lock (e) { return _now() < e.LockedUntil; }
    }

    /// <summary>Record a failed attempt; may transition the key into lockout.</summary>
    public void RecordFailure(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var now = _now();

        // GetOrAdd hands back a single shared Entry; the read-modify-write below MUST be under the
        // entry lock. The previous AddOrUpdate mutated the entry in place and returned the same
        // reference, so ConcurrentDictionary did a no-op reference "update" with no serialization —
        // two concurrent failures both did a non-atomic Failures++ and one increment was lost, which
        // let concurrent guessing slip under the lockout threshold (review: cookie-auth finding #1).
        var entry = _entries.GetOrAdd(key, _ => new Entry { WindowStart = now });
        lock (entry)
        {
            // Fresh window if the previous one elapsed (and we are not already locked).
            if (now >= entry.LockedUntil && now - entry.WindowStart > Window)
            {
                entry.Failures = 0;
                entry.WindowStart = now;
            }
            entry.Failures++;
            if (entry.Failures >= MaxAttempts)
            {
                entry.LockedUntil = now + LockoutDuration;
                entry.Failures = 0;          // reset so the next lockout needs a fresh burst
                entry.WindowStart = now + LockoutDuration;
            }
        }
    }

    /// <summary>Clear any failure/lockout state for <paramref name="key"/> after a successful login.</summary>
    public void RecordSuccess(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _entries.TryRemove(key, out _);
    }
}
