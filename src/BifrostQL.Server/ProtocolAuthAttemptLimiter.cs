using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
        /// new key — the cap cannot be turned into a bypass. Past the cap a NEW key is counted in
        /// its axis's shared overflow window instead (one per axis, outside the map), under the
        /// same per-axis cap: every untracked source shares one source budget and every untracked
        /// account one account budget. That keeps memory bounded and the hot path O(1) amortized
        /// while a saturated map still limits; the cost of a flood falls on new callers during it,
        /// never on an already-tracked decision.
        /// </summary>
        internal const int DefaultMaxTrackedKeys = 20_000;

        private readonly int _maxPerSource;
        private readonly int _maxPerAccount;
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly int _maxTrackedKeys;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
        // The shared windows for keys the full map cannot track, keyed by axis ("s:"/"a:").
        private readonly ConcurrentDictionary<string, Window> _overflow = new(StringComparer.Ordinal);
        // Plain object + Monitor rather than System.Threading.Lock: this assembly also targets
        // net8.0, where that type does not exist.
        private readonly object _sweepGate = new();
        private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

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

        /// <summary>The tracked (axis+key) strings — for tests asserting what a key costs to hold.</summary>
        internal IEnumerable<string> TrackedKeys => _windows.Keys;

        /// <summary>
        /// Counts one attempt against its source window and (when <paramref name="account"/> is
        /// non-null) its account window, and returns whether it is admitted. False when EITHER
        /// axis is already at its cap for the current window. An admitted attempt increments the
        /// counters; a refused attempt increments none, so a refusal on one axis cannot be
        /// leveraged to inflate the other.
        /// </summary>
        protected bool TryAdmit(string source, string? account) => TryAdmit(source, account, out _);

        /// <summary>
        /// <see cref="TryAdmit(string, string?)"/>, also returning the slots the admitted attempt
        /// took so a caller can give them back with <see cref="Refund"/> once the credential
        /// verifies. Without the refund, a client that authenticates on every request (HTTP Basic,
        /// where each request carries the password) spends the window on its own good logins and
        /// is refused at the cap, and anyone who knows the username can top the account window
        /// up to that cap from the victim's own traffic.
        /// </summary>
        protected bool TryAdmit(string source, string? account, out Admission admission)
        {
            admission = default;
            var accountKey = account is null ? null : $"a:{HashAccountKey(CanonicalAccountName(account))}";
            var sourceKey = $"s:{source}";
            var now = _clock();
            var sourceWindow = Track(sourceKey, "s:", now);
            var accountWindow = accountKey is null ? default : Track(accountKey, "a:", now);
            // Each axis is reserved atomically: check-and-increment in one compare-and-swap, so N
            // concurrent attempts against a window at cap-1 admit exactly one, not N. A separate
            // read then increment let every in-flight attempt pass the read before any counted.
            if (!TryReserve(sourceWindow, _maxPerSource, now, out var sourceStart)) return false;
            var accountStart = default(DateTimeOffset);
            if (accountKey is not null && !TryReserve(accountWindow, _maxPerAccount, now, out accountStart))
            {
                // A refusal on the account axis must not consume the source axis.
                Release(sourceWindow, sourceStart);
                return false;
            }
            admission = new Admission(
                new Slot(sourceWindow.Map, sourceWindow.Key, sourceStart),
                accountKey is null ? null : new Slot(accountWindow.Map, accountWindow.Key, accountStart));
            return true;
        }

        /// <summary>
        /// Gives back the slots <paramref name="admission"/> took, on both axes, after the
        /// credential verified. Each slot is released only while its window is still the one it
        /// was taken in (the same compare-and-swap <see cref="TryAdmit(string, string?)"/> uses), so
        /// a refund never credits a later window. A default <see cref="Admission"/> refunds nothing.
        /// Failed attempts are never refunded: they are what the window counts.
        /// </summary>
        protected void Refund(in Admission admission)
        {
            if (admission.Source is { } source)
                Release((source.Map, source.Key), source.Start);
            if (admission.Account is { } account)
                Release((account.Map, account.Key), account.Start);
        }

        /// <summary>The window slot one admitted attempt took on one axis.</summary>
        internal readonly record struct Slot(ConcurrentDictionary<string, Window> Map, string Key, DateTimeOffset Start);

        /// <summary>
        /// The slots an admitted attempt took, handed back to the caller of
        /// <see cref="TryAdmit(string, string?, out Admission)"/> so it can <see cref="Refund"/>
        /// them on success. Opaque outside the limiter.
        /// </summary>
        internal readonly struct Admission
        {
            internal Admission(Slot source, Slot? account)
            {
                Source = source;
                Account = account;
            }

            internal Slot? Source { get; }
            internal Slot? Account { get; }
        }

        /// <summary>
        /// Picks the window an attempt is counted in: the key's own when it is tracked or the map
        /// has room (rolled-over counters are reclaimed first — <see cref="TryReserve"/> already treats
        /// them as empty, so dropping them changes no decision), else its axis's shared overflow
        /// window. Existing counters are never evicted, so a live decision is never dropped.
        /// </summary>
        private (ConcurrentDictionary<string, Window> Map, string Key) Track(string key, string axis, DateTimeOffset now)
        {
            if (_windows.ContainsKey(key) || _windows.Count < _maxTrackedKeys)
                return (_windows, key);
            TrySweepExpired(now);
            return _windows.Count < _maxTrackedKeys ? (_windows, key) : (_overflow, axis);
        }

        /// <summary>
        /// Maps the wire-supplied account name to its canonical comparison form. The default is
        /// the raw string; an adapter whose account names have a canonical form (an LDAP bind DN)
        /// overrides this so every respelling of one account shares ONE window.
        /// </summary>
        protected virtual string CanonicalAccountName(string account) => account;

        /// <summary>
        /// Every account key is stored as the SHA-256 of its canonical name, here in the base so
        /// no adapter can forget it. The name is attacker-controlled and arrives before
        /// authentication: tracked raw, a username near the frame cap (1 MiB) made each of the
        /// up-to-<see cref="DefaultMaxTrackedKeys"/> counters cost megabytes.
        /// </summary>
        private static string HashAccountKey(string canonical) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        /// <summary>
        /// Takes one slot in the window when it is under <paramref name="cap"/>, atomically. A
        /// rolled-over window restarts at this attempt. <paramref name="start"/> names the window the
        /// slot was taken in, so <see cref="Release"/> never gives a slot back to a later window.
        /// </summary>
        private bool TryReserve(
            (ConcurrentDictionary<string, Window> Map, string Key) window, int cap, DateTimeOffset now, out DateTimeOffset start)
        {
            start = default;
            if (cap < 1)
                return false;
            while (true)
            {
                if (!window.Map.TryGetValue(window.Key, out var seen))
                {
                    if (window.Map.TryAdd(window.Key, new Window(now, 1)))
                    {
                        start = now;
                        return true;
                    }
                    continue;
                }
                var live = now - seen.Start < _window ? seen : new Window(now, 0);
                if (live.Count >= cap)
                    return false;
                if (window.Map.TryUpdate(window.Key, live with { Count = live.Count + 1 }, seen))
                {
                    start = live.Start;
                    return true;
                }
            }
        }

        /// <summary>Gives back a slot taken by <see cref="TryReserve"/>, if its window is still current.</summary>
        private static void Release((ConcurrentDictionary<string, Window> Map, string Key) window, DateTimeOffset start)
        {
            while (window.Map.TryGetValue(window.Key, out var seen) && seen.Start == start && seen.Count > 0)
            {
                if (window.Map.TryUpdate(window.Key, seen with { Count = seen.Count - 1 }, seen))
                    return;
            }
        }

        private void TrySweepExpired(DateTimeOffset now)
        {
            // At most one full-map scan per window, one thread at a time: a sustained at-cap flood
            // costs O(1) amortized per attempt, never an O(n) scan on every one. A thread that
            // finds the sweep running (or run too recently) skips it and the caller counts the new
            // key in the overflow window — correctness never depends on the sweep firing.
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

        internal readonly record struct Window(DateTimeOffset Start, int Count);
    }
}
