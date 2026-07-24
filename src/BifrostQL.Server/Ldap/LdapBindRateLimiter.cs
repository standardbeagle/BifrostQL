using System.Collections.Concurrent;

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
        private readonly int _maxPerSource;
        private readonly int _maxPerAccount;
        private readonly TimeSpan _window;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

        public LdapBindRateLimiter(int maxPerSource, int maxPerAccount, TimeSpan window, Func<DateTimeOffset>? clock = null)
        {
            if (maxPerSource < 1) throw new ArgumentOutOfRangeException(nameof(maxPerSource));
            if (maxPerAccount < 1) throw new ArgumentOutOfRangeException(nameof(maxPerAccount));
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
            _maxPerSource = maxPerSource;
            _maxPerAccount = maxPerAccount;
            _window = window;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

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

        private void Increment(string key, DateTimeOffset now) =>
            _windows.AddOrUpdate(
                key,
                _ => new Window(now, 1),
                (_, w) => now - w.Start < _window ? w with { Count = w.Count + 1 } : new Window(now, 1));

        private readonly record struct Window(DateTimeOffset Start, int Count);
    }
}
