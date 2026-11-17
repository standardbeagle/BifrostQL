using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Utils;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// The local-auth login subtype of <see cref="ProtocolAuthAttemptLimiter"/>. Every login
    /// attempt is counted against its client IP and against the login it names BEFORE the
    /// password hash is verified, and refused when either window is at its cap. Counting at
    /// admission (not after a failed verify) means N concurrent requests cannot all pass the
    /// check before the first failure is recorded, and the per-account axis bounds guessing
    /// spread over many source IPs. Past the limiter's tracked-key cap, new keys share the
    /// axis's overflow window rather than going untracked.
    ///
    /// <para>One instance per host (a DI singleton registered by <c>AddBifrostLocalAuth</c>), never
    /// shared with another front door's budget. Per-process only: a load-balanced deployment
    /// throttles per node and needs an edge rate limiter for cross-node guarantees.</para>
    /// </summary>
    internal sealed class LocalLoginRateLimiter : ProtocolAuthAttemptLimiter
    {
        public LocalLoginRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        public static LocalLoginRateLimiter From(LocalAuthOptions options) =>
            new(options.MaxLoginAttemptsPerSource, options.MaxLoginAttemptsPerAccount, options.LoginAttemptWindow);

        /// <summary>Counts one login attempt; false when the source or the account is at its cap.</summary>
        public bool TryLogin(string source, string login) => TryAdmit(source, login);

        // One window per login however it is spelled (case, surrounding whitespace), hashed so
        // an attacker-supplied login of any length costs a fixed-size key.
        protected override string NormalizeAccountKey(string account) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StringNormalizer.Normalize(account))));
    }
}
