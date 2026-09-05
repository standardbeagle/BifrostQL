namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The RESP per-adapter subtype of <see cref="ProtocolAuthAttemptLimiter"/> for AUTH attempts,
    /// bounded on TWO independent axes so neither one hostile source nor a distributed guess at one
    /// login runs unbounded: a per-SOURCE cap (one peer spraying many accounts) and a per-ACCOUNT
    /// cap (many peers hammering one login). An attempt is refused when EITHER window is already at
    /// its cap — BEFORE the credential is resolved or compared, so a refusal costs nothing and
    /// cannot itself be turned into a work amplifier.
    /// </summary>
    internal sealed class RespAuthRateLimiter : ProtocolAuthAttemptLimiter
    {
        public RespAuthRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        /// <summary>
        /// Counts one authentication attempt against both its source and account windows and
        /// returns whether it is admitted. False when EITHER axis is already at its cap for the
        /// current window.
        /// </summary>
        public bool TryAttempt(string source, string account) => TryAdmit(source, account);
    }
}
