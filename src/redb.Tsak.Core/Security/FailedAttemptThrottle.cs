using System.Collections.Concurrent;

namespace redb.Tsak.Core.Security;

/// <summary>
/// Per-key failed-attempt throttle (review item 4.5). After <see cref="MaxAttempts"/> failures within
/// <see cref="Window"/> the key is locked out for <see cref="LockoutDuration"/>, defeating online guessing.
/// A success clears the counter, so a legitimate caller with a valid credential never accumulates state.
/// <para>
/// This is the API-key analogue of the dashboard's <c>LoginThrottle</c> (review item 2.6): the same
/// failure-count-and-lockout semantics and the same per-entry lock discipline (see the concurrency note in
/// <see cref="RecordFailure"/>). It gates the ACTUAL authentication surface — every API request carries a
/// key, so the throttle keys on the client IP and counts key-auth failures across all endpoints, not just a
/// single path prefix (which was the 4.5 defect: a path-limited request-count gate that key-guessing simply
/// routed around).
/// </para>
/// <para>Pure and clock-injectable so lockout timing is unit-testable without waiting.</para>
/// <para><b>Known limitation (F3):</b> because a success clears the key's counter, principals that share
/// one key (NAT / corporate egress / a single reverse-proxy IP when <c>TrustProxyHeaders</c> is off) can
/// let a legitimate user's success reset an attacker's failure count. Key on the real per-client IP
/// (enable <c>TrustProxyHeaders</c> behind a proxy) to avoid the shared-bucket weakening.</para>
/// </summary>
public sealed class FailedAttemptThrottle
{
    // Above this many tracked keys, RecordFailure opportunistically evicts expired entries so a burst of
    // distinct source IPs (e.g. an IPv6 /64) cannot grow the map without bound (review item 4.5 / F4).
    private const int SweepThreshold = 10_000;

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

    public FailedAttemptThrottle(
        int maxAttempts = 10,
        TimeSpan? window = null,
        TimeSpan? lockoutDuration = null,
        Func<DateTimeOffset>? now = null)
    {
        MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        Window = window ?? TimeSpan.FromSeconds(60);
        LockoutDuration = lockoutDuration ?? TimeSpan.FromSeconds(120);
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

        // GetOrAdd hands back a single shared Entry; the read-modify-write below MUST be under the entry
        // lock. A mutate-in-place AddOrUpdate returns the same reference, so ConcurrentDictionary does a
        // no-op reference "update" with no serialization — two concurrent failures both do a non-atomic
        // Failures++ and one increment is lost, letting concurrent guessing slip under the lockout
        // threshold (same class of bug fixed for LoginThrottle — cookie-auth finding #1).
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

        // Opportunistic eviction: only pays the O(n) scan once the map is large, keeping steady-state
        // RecordFailure O(1) while bounding memory against a flood of distinct source IPs (F4).
        if (_entries.Count > SweepThreshold)
            SweepExpired(now);
    }

    /// <summary>Removes entries whose lockout has elapsed and whose failure window has rolled off — i.e.
    /// keys that <see cref="IsLockedOut"/> would report as not-locked and <see cref="RecordFailure"/> would
    /// treat as a fresh window anyway. Dropping them changes no observable behaviour.</summary>
    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var kv in _entries)
        {
            var e = kv.Value;
            bool dead;
            lock (e) { dead = now >= e.LockedUntil && now - e.WindowStart > Window; }
            if (dead)
                _entries.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Clear any failure/lockout state for <paramref name="key"/> after a success.</summary>
    public void RecordSuccess(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _entries.TryRemove(key, out _);
    }
}
