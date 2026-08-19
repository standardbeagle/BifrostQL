using System.Collections.Concurrent;
using System.Threading;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// A fixed-window rate limiter for bind attempts, bounded on TWO independent axes so neither a
    /// single hostile source nor a brute-force against a single account can run unbounded (criterion
    /// 3): a per-source cap (one client IP spraying many accounts) and a per-account cap (many clients
    /// hammering one DN). Every attempt counts against both its source key and its account key; when
    /// either window count is already at its cap the attempt is refused BEFORE any adaptive-hash work,
    /// so a rate-limit trip costs essentially nothing (it is not itself a hash-DoS vector).
    ///
    /// <para>Counters live in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by axis+key and
    /// reset when their window rolls over. This is a per-process guard on the unauthenticated bind
    /// path; a deployment that needs cross-node limiting layers its own lockout policy through
    /// <see cref="ILdapBindObserver"/>.</para>
    /// </summary>
    internal sealed class LdapBindRateLimiter
    {
        // Hard cap on tracked (axis+key) counters. Entries are never removed on their own — a
        // fresh source or account key is created per distinct value — so an account-spraying or
        // IP-churning peer would grow this map without bound on the unauthenticated bind path.
        // Increment enforces the cap on INSERT: an already-tracked counter always updates in place
        // (a live rate-limit decision is never evicted, so the cap can never be turned into a
        // bypass), but a brand-new counter past the cap is refused after a throttled sweep of
        // rolled-over counters fails to free a slot. Beyond the cap the per-process limiter degrades
        // to best-effort (an untracked key reads as 0 and is admitted) — the deployment layers its
        // own cross-node lockout for that regime (see the type summary) — but memory stays bounded
        // and the hot path stays O(1) amortized (the O(n) sweep runs at most once per window).
        internal const int DefaultMaxTrackedKeys = 20_000;

        private readonly int _maxPerSource;
        private readonly int _maxPerAccount;
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly int _maxTrackedKeys;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
        private readonly object _sweepGate = new();
        private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

        public LdapBindRateLimiter(
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
        /// Counts one bind attempt against both its source and account windows and returns whether it
        /// is admitted. Returns false if EITHER axis is already at its cap for the current window. An
        /// admitted attempt increments both counters; a refused attempt increments neither (so a
        /// refusal cannot be leveraged to inflate the other axis).
        /// </summary>
        public bool TryBind(string source, string account)
        {
            var now = _clock();
            // Check both axes first without mutating, so a trip on one axis does not consume the other.
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
            // Already-tracked counter: update (or roll over) in place. Existing counters are never
            // evicted by the cap, so a live rate-limit decision can never be dropped to admit a new key.
            if (_windows.ContainsKey(key))
            {
                _windows.AddOrUpdate(
                    key,
                    _ => new Window(now, 1),
                    (_, w) => now - w.Start < _window ? w with { Count = w.Count + 1 } : new Window(now, 1));
                return;
            }

            // New counter: enforce the hard cap BEFORE inserting. Reclaim rolled-over counters first
            // (Peek treats them as 0 anyway, so dropping them changes no decision); if the map is
            // still full of LIVE counters, refuse to track this new key rather than evict a live
            // counter or grow without bound.
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
            // At most one full-map scan per window, and one thread at a time: a sustained at-cap flood
            // costs O(1) amortized per Increment, never an O(n) scan on every attempt. A thread that
            // finds the sweep already running (or run too recently) skips it and the caller simply
            // refuses the new key — correctness never depends on the sweep firing.
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
                        // Conditional remove: Window is a value type, so this drops the counter only
                        // if it has not been replaced by a concurrent Increment since we read it.
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
