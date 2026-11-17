namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// The pgwire per-adapter subtype of <see cref="ProtocolAuthAttemptLimiter"/> for
    /// authentication attempts (SCRAM and cleartext alike), bounded per connection source (client
    /// IP) and per login name. Either method costs PBKDF2(4096) of CPU, so without a per-source
    /// bound a single unauthenticated peer can open connections in a loop and burn CPU at will;
    /// without a per-account bound many peers can guess one login without limit, because each
    /// connection carries one attempt and reconnecting is free. The refusal happens BEFORE any
    /// credential lookup or hash work, so a tripped limit costs essentially nothing.
    /// </summary>
    internal sealed class PgAuthRateLimiter : ProtocolAuthAttemptLimiter
    {
        public PgAuthRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        /// <summary>
        /// Counts one auth attempt against its source and account windows and returns whether it
        /// is admitted. False when EITHER axis is already at its cap for the current window; a
        /// refused attempt increments neither counter.
        /// </summary>
        public bool TryAcquire(string source, string user) => TryAdmit(source, user);
    }
}
