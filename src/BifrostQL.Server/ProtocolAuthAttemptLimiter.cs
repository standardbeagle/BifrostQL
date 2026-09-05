using System.Collections.Concurrent;
using System.Threading;

namespace BifrostQL.Server
{
    /// <summary>
    /// A fixed-window cap on pre-auth attempts, bounded on TWO independent axes so neither one
    /// hostile source nor a distributed guess at one account runs unbounded: a per-SOURCE cap
    /// (one peer spraying many accounts) and a per-ACCOUNT cap (many peers hammering one login).
    /// An attempt is counted against both and refused when EITHER window is already at its cap —
    /// BEFORE the credential is resolved or compared, so a refusal costs nothing and cannot itself
    /// be turned into a work amplifier.
    ///
    /// <para>This is the ONE implementation of the pre-auth attempt window for every front door
    /// (H11 follow-up: the arithmetic was copied three times — LDAP bind, RESP AUTH, pgwire SCRAM —
    /// and three copies of one security decision drift). Each adapter keeps its own SUBTYPE
    /// (<c>LdapBindRateLimiter</c>, <c>RespAuthRateLimiter</c>, <c>PgAuthRateLimiter</c>) with its
    /// own instance and budget, exactly as <see cref="ProtocolConnectionLimiter"/> does: a limiter
    /// instance shared between two front doors would let traffic on one consume the other's
    /// budget.</para>
    ///
    /// <para>Counters live in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by axis+key
    /// and roll over with their window. This is a per-process guard on the unauthenticated path; a
    /// deployment needing cross-node lockout layers its own policy on top.</para>
    /// </summary>
    internal abstract class ProtocolAuthAttemptLimiter
    {
        /// <summary>
        /// Hard cap on tracked (axis+key) counters. Nothing removes an entry on its own and a fresh
        /// key is created per distinct source or account, so an account-spraying or
        /// address-churning peer would otherwise grow this map without bound on the
        /// unauthenticated path. The cap is enforced on INSERT only: an already-tracked counter
        /// always updates in place, so a live rate-limit decision can never be evicted to admit a
        /// new key — the cap cannot be turned into a bypass. Past the cap the limiter degrades to
        /// best-effort for NEW keys (an untracked key reads as 0), which keeps memory bounded and
        /// the hot path O(1) amortized.
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

        /// <summary>
        /// A subtype with no per-account axis (pgwire, where the one-attempt-per-connection shape
        /// already bounds the account axis) passes <paramref name="maxPerAccount"/> as
        /// <see cref="int.MaxValue"/> and admits with a null account.
        /// </summary>
        protected ProtocolAuthAttemptLimiter(
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
        /// Counts one attempt against its source window and (when <paramref name="account"/> is
        /// non-null) its account window, and returns whether it is admitted. False when EITHER
        /// axis is already at its cap for the current window. An admitted attempt increments the
        /// counters; a refused attempt increments none, so a refusal on one axis cannot be
        /// leveraged to inflate the other.
        /// </summary>
        protected bool TryAdmit(string source, string? account)
        {
            var accountKey = account is null ? null : $"a:{NormalizeAccountKey(account)}";
            var sourceKey = $"s:{source}";
            var now = _clock();
            // Check both axes first without mutating, so a trip on one axis does not consume the other.
            if (Peek(sourceKey, now) >= _maxPerSource) return false;
            if (accountKey is not null && Peek(accountKey, now) >= _maxPerAccount) return false;
            Increment(sourceKey, now);
            if (accountKey is not null) Increment(accountKey, now);
            return true;
        }

        /// <summary>
        /// Maps the wire-supplied account name to its tracked key. The default tracks the raw
        /// string; an adapter whose account names have a canonical comparison form (an LDAP bind
        /// DN) overrides this so every respelling of one account shares ONE window.
        /// </summary>
        protected virtual string NormalizeAccountKey(string account) => account;

        private int Peek(string key, DateTimeOffset now) =>
            _windows.TryGetValue(key, out var w) && now - w.Start < _window ? w.Count : 0;

        private void Increment(string key, DateTimeOffset now)
        {
            // Already-tracked counter: update (or roll over) in place. Existing counters are never
            // evicted by the cap, so a live rate-limit decision can never be dropped to admit a
            // new key.
            if (_windows.ContainsKey(key))
            {
                Bump(key, now);
                return;
            }

            // New counter: enforce the hard cap BEFORE inserting. Rolled-over counters are
            // reclaimed first (Peek already treats them as 0, so dropping them changes no
            // decision); if the map is still full of LIVE counters, refuse to track the new key
            // rather than evict a live one or grow without bound.
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
            // costs O(1) amortized per attempt, never an O(n) scan on every one. A thread that
            // finds the sweep running (or run too recently) skips it and the caller simply
            // declines to track the new key — correctness never depends on the sweep firing.
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
                        // Conditional remove: Window is a value type, so the counter is dropped
                        // only if a concurrent attempt has not replaced it since we read it.
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
