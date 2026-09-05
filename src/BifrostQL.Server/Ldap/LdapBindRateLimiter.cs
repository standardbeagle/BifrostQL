using System.Security.Cryptography;
using System.Text;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// The LDAP per-adapter subtype of <see cref="ProtocolAuthAttemptLimiter"/> for bind attempts,
    /// bounded on TWO independent axes so neither a single hostile source nor a brute-force against
    /// a single account can run unbounded: a per-source cap (one client IP spraying many accounts)
    /// and a per-account cap (many clients hammering one DN). Every attempt is refused BEFORE any
    /// adaptive-hash work when either window is at its cap, so a rate-limit trip costs essentially
    /// nothing (it is not itself a hash-DoS vector).
    ///
    /// <para>A deployment that needs cross-node limiting layers its own lockout policy through
    /// <see cref="ILdapBindObserver"/>.</para>
    /// </summary>
    internal sealed class LdapBindRateLimiter : ProtocolAuthAttemptLimiter
    {
        public LdapBindRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        /// <summary>
        /// Counts one bind attempt against both its source and account windows and returns whether
        /// it is admitted. Returns false if EITHER axis is already at its cap for the current
        /// window.
        /// </summary>
        public bool TryBind(string source, string account) => TryAdmit(source, account);

        // The per-account key is the DN's canonical comparison form (RFC 4514: attribute types and
        // values match case-insensitively, separator whitespace is insignificant), hashed. The
        // canonical form makes every respelling of one DN share ONE window — keyed on the raw
        // string, "cn=Alice, dc=x" and "CN=alice,dc=x" would each get a fresh per-account cap and
        // the brute-force bound would never trip. The hash bounds the tracked key's length: a bind
        // DN may run to nearly MaxMessageLength, and an unbounded attacker-controlled key would let
        // each tracked counter cost ~1 MiB.
        protected override string NormalizeAccountKey(string account)
        {
            var canonical = LdapDn.CanonicalKey(account) ?? account;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }
}
