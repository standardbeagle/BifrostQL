using System.Collections.Concurrent;
using System.Threading;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// A fixed-window cap on authentication attempts, bounded on TWO independent axes so neither one
    /// hostile source nor a distributed guess at one login runs unbounded: a per-SOURCE cap (one peer
    /// spraying many accounts) and a per-ACCOUNT cap (many peers hammering one login). An attempt is
    /// counted against both and refused when EITHER window is already at its cap — BEFORE the
    /// credential is resolved or compared, so a refusal costs nothing and cannot itself be turned
    /// into a work amplifier.
    ///
    /// <para>Structurally this is the LDAP bind limiter, applied to the RESP AUTH path for the same
    /// reason. It is deliberately RESP-owned rather than shared: a limiter instance shared between
    /// two front doors would let traffic on one consume the other's budget, exactly the trap the
    /// per-adapter connection-limiter subtypes exist to avoid.</para>
    ///
    /// <para>Counters live in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by axis+key and
    /// roll over with their window. This is a per-process guard on the unauthenticated path; a
    /// deployment needing cross-node lockout layers its own policy on top.</para>
    /// </summary>
    internal sealed class RespAuthRateLimiter
    {
        /// <summary>
        /// Hard cap on tracked (axis+key) counters. Nothing removes an entry on its own and a fresh
        /// key is created per distinct source or account, so an account-spraying or address-churning
        /// peer would otherwise grow this map without bound on the unauthenticated path. The cap is
        /// enforced on INSERT only: an already-tracked counter always updates in place, so a live
        /// rate-limit decision can never be evicted to admit a new key — the cap cannot be turned
        /// into a bypass. Past the cap the limiter degrades to best-effort for NEW keys (an untracked
        /// key reads as 0), which keeps memory bounded and the hot path O(1) amortized.
        /// </summary>
        internal const int DefaultMaxTrackedKeys = 20_000;

        private readonly int _maxPerSource;
        private readonly int _maxPerAccount;
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly int _maxTrackedKeys;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
        // Plain object + Monitor rather than System.Threading.Lock: this assembly also targets
        // net8.0, where that type does not exist.
        private readonly object _sweepGate = new();
        private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

        public RespAuthRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
        {
            if (maxPerSource < 1) throw new ArgumentOutOfRangeException(nameof(maxPerSource));
            if (maxPerAccount < 1) throw new ArgumentOutOfRangeException(nameof(maxPerAccount));
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            if (maxTrackedKeys < 1) throw new ArgumentOutOfRangeException(nameof(maxTrackedKeys));
            _maxPerSource = maxPerSource;
            _maxPerAccount = maxPerAccount;
            _window = window;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _maxTrackedKeys = maxTrackedKeys;
        }

        /// <summary>Number of tracked counters — for tests asserting the map stays bounded.</summary>
        internal int TrackedKeyCount => _windows.Count;

        /// <summary>
        /// Counts one authentication attempt against both its source and account windows and returns
        /// whether it is admitted. False when EITHER axis is already at its cap for the current
        /// window. An admitted attempt increments both counters; a refused attempt increments
        /// neither, so a refusal on one axis cannot be leveraged to inflate the other.
        /// </summary>
        public bool TryAttempt(string source, string account)
        {
            var now = _clock();
            if (Peek($"s:{source}", now) >= _maxPerSource) return false;
            if (Peek($"a:{account}", now) >= _maxPerAccount) return false;
            Increment($"s:{source}", now);
            Increment($"a:{account}", now);
            return true;
        }

        private int Peek(string key, DateTimeOffset now) =>
            _windows.TryGetValue(key, out var w) && now - w.Start < _window ? w.Count : 0;

        private void Increment(string key, DateTimeOffset now)
        {
            if (_windows.ContainsKey(key))
            {
                Bump(key, now);
                return;
            }

            // New counter: enforce the hard cap BEFORE inserting. Rolled-over counters are reclaimed
            // first (Peek already treats them as 0, so dropping them changes no decision); if the map
            // is still full of LIVE counters, refuse to track the new key rather than evict a live
            // one or grow without bound.
            if (_windows.Count >= _maxTrackedKeys)
            {
                TrySweepExpired(now);
                if (_windows.Count >= _maxTrackedKeys)
                    return;
            }

            Bump(key, now);
        }

        private void Bump(string key, DateTimeOffset now) =>
            _windows.AddOrUpdate(
                key,
                _ => new Window(now, 1),
                (_, w) => now - w.Start < _window ? w with { Count = w.Count + 1 } : new Window(now, 1));

        private void TrySweepExpired(DateTimeOffset now)
        {
            // At most one full-map scan per window, one thread at a time: a sustained at-cap flood
            // costs O(1) amortized per attempt, never an O(n) scan on every one. A thread that finds
            // the sweep running (or run too recently) skips it and the caller simply declines to
            // track the new key — correctness never depends on the sweep firing.
            if (!Monitor.TryEnter(_sweepGate))
                return;
            try
            {
                if (now - _lastSweep < _window)
                    return;
                _lastSweep = now;
                foreach (var entry in _windows)
                {
                    if (now - entry.Value.Start >= _window)
                        // Conditional remove: Window is a value type, so the counter is dropped only
                        // if a concurrent attempt has not replaced it since we read it.
                        _windows.TryRemove(entry);
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
