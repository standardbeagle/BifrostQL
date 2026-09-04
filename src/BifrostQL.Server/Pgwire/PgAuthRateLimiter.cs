using System.Collections.Concurrent;
using System.Threading;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A fixed-window rate limiter for SCRAM authentication attempts, keyed by connection
    /// source (client IP). One SCRAM verification costs PBKDF2(4096) of CPU, so without a
    /// per-source bound a single unauthenticated peer can open connections in a loop and
    /// burn CPU at will. The refusal happens BEFORE any credential lookup or hash work, so
    /// a tripped limit costs essentially nothing. This is the per-source half of the LDAP
    /// bind limiter's two axes (see <c>LdapBindRateLimiter</c>); pgwire needs only the
    /// source axis because each connection carries at most one attempt and the per-account
    /// brute-force axis is already bounded by the one-attempt-per-connection shape.
    ///
    /// <para>The map is hard-capped: an already-tracked source always updates in place (a
    /// live limit decision is never evicted into a bypass), and a brand-new source past the
    /// cap is refused tracking after a throttled sweep of rolled-over windows fails to free
    /// a slot. Past the cap the limiter degrades to best-effort for UNTRACKED sources —
    /// memory stays bounded and the hot path stays O(1) amortized.</para>
    /// </summary>
    internal sealed class PgAuthRateLimiter
    {
        internal const int DefaultMaxTrackedKeys = 20_000;

        private readonly int _maxPerSource;
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly int _maxTrackedKeys;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
        private readonly object _sweepGate = new();
        private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

        public PgAuthRateLimiter(
            int maxPerSource, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
        {
            if (maxPerSource < 1) throw new ArgumentOutOfRangeException(nameof(maxPerSource));
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            if (maxTrackedKeys < 1) throw new ArgumentOutOfRangeException(nameof(maxTrackedKeys));
            _maxPerSource = maxPerSource;
            _window = window;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _maxTrackedKeys = maxTrackedKeys;
        }

        /// <summary>Number of tracked source counters — for tests asserting the map stays bounded.</summary>
        internal int TrackedKeyCount => _windows.Count;

        /// <summary>
        /// Counts one auth attempt against the source's window and returns whether it is
        /// admitted. Returns false when the source is already at its cap for the current
        /// window; a refused attempt does not increment the counter.
        /// </summary>
        public bool TryAcquire(string source)
        {
            var now = _clock();
            if (Peek(source, now) >= _maxPerSource) return false;
            Increment(source, now);
            return true;
        }

        private int Peek(string key, DateTimeOffset now) =>
            _windows.TryGetValue(key, out var w) && now - w.Start < _window ? w.Count : 0;

        private void Increment(string key, DateTimeOffset now)
        {
            // Already-tracked counter: update (or roll over) in place. Existing counters are
            // never evicted by the cap, so a live rate-limit decision can never be dropped to
            // admit a new key.
            if (_windows.ContainsKey(key))
            {
                _windows.AddOrUpdate(
                    key,
                    _ => new Window(now, 1),
                    (_, w) => now - w.Start < _window ? w with { Count = w.Count + 1 } : new Window(now, 1));
                return;
            }

            // New counter: enforce the hard cap BEFORE inserting. Reclaim rolled-over counters
            // first; if the map is still full of LIVE counters, refuse to track the new key
            // rather than evict a live counter or grow without bound.
            if (_windows.Count >= _maxTrackedKeys)
            {
                TrySweepExpired(now);
                if (_windows.Count >= _maxTrackedKeys)
                    return;
            }

            _windows.AddOrUpdate(
                key,
                _ => new Window(now, 1),
                (_, w) => now - w.Start < _window ? w with { Count = w.Count + 1 } : new Window(now, 1));
        }

        private void TrySweepExpired(DateTimeOffset now)
        {
            // At most one full-map scan per window, one thread at a time: a sustained at-cap
            // flood costs O(1) amortized per Increment, never an O(n) scan per attempt.
            if (!Monitor.TryEnter(_sweepGate))
                return;
            try
            {
                if (now - _lastSweep < _window)
                    return;
                _lastSweep = now;
                foreach (var kvp in _windows)
                {
                    if (now - kvp.Value.Start >= _window)
                        _windows.TryRemove(kvp);
                }
            }
            finally
            {
                Monitor.Exit(_sweepGate);
            }
        }

        private readonly record struct Window(DateTimeOffset Start, int Count);
    }
}
