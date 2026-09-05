namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// The pgwire per-adapter subtype of <see cref="ProtocolAuthAttemptLimiter"/> for SCRAM
    /// authentication attempts, keyed by connection source (client IP). One SCRAM verification
    /// costs PBKDF2(4096) of CPU, so without a per-source bound a single unauthenticated peer can
    /// open connections in a loop and burn CPU at will. The refusal happens BEFORE any credential
    /// lookup or hash work, so a tripped limit costs essentially nothing.
    ///
    /// <para>Only the source axis is used: each connection carries at most one attempt, so the
    /// per-account brute-force axis is already bounded by the one-attempt-per-connection shape.
    /// The base's account axis is admitted with a null account and never tracked here.</para>
    /// </summary>
    internal sealed class PgAuthRateLimiter : ProtocolAuthAttemptLimiter
    {
        public PgAuthRateLimiter(
            int maxPerSource, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, int.MaxValue, window, clock, maxTrackedKeys)
        {
        }

        /// <summary>
        /// Counts one auth attempt against the source's window and returns whether it is
        /// admitted. Returns false when the source is already at its cap for the current
        /// window; a refused attempt does not increment the counter.
        /// </summary>
        public bool TryAcquire(string source) => TryAdmit(source, null);
    }
}
