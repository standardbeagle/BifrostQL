namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The OData subtype of <see cref="ProtocolAuthAttemptLimiter"/> for Basic credentials,
    /// bounded per SOURCE (one client spraying many accounts) and per ACCOUNT (many clients
    /// guessing one login). An attempt over either cap is refused BEFORE the credential lookup
    /// and before the PBKDF2 verification, so a refusal costs no hashing and its answer does
    /// not depend on whether the account exists.
    /// </summary>
    internal sealed class ODataBasicAuthRateLimiter : ProtocolAuthAttemptLimiter
    {
        public ODataBasicAuthRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        /// <summary>Counts one Basic attempt; false when either window is already at its cap.</summary>
        public bool TryAttempt(string source, string account) => TryAdmit(source, account);
    }
}
