namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// The local-auth second-factor subtype of <see cref="ProtocolAuthAttemptLimiter"/>. Every code or
    /// recovery code presented (login step two, enrollment confirm, disable, regenerate) is counted
    /// against its client IP and the account it targets BEFORE the code is checked. Its own instance
    /// and budget, separate from <see cref="LocalLoginRateLimiter"/>, so password guessing cannot
    /// spend the code budget and the reverse. Wrong codes also count toward the account lockout.
    ///
    /// <para>The account key is the app-user id (from the challenge ticket or the session), or for a
    /// ticket that names no account, the ticket's hash: an unknown ticket then costs only its source
    /// budget. Per-process only, like the login limiter.</para>
    /// </summary>
    internal sealed class LocalMfaRateLimiter : ProtocolAuthAttemptLimiter
    {
        public LocalMfaRateLimiter(
            int maxPerSource, int maxPerAccount, TimeSpan window,
            Func<DateTimeOffset>? clock = null, int maxTrackedKeys = DefaultMaxTrackedKeys)
            : base(maxPerSource, maxPerAccount, window, clock, maxTrackedKeys)
        {
        }

        public static LocalMfaRateLimiter From(LocalAuthOptions options) =>
            new(options.MaxMfaAttemptsPerSource, options.MaxMfaAttemptsPerAccount, options.LoginAttemptWindow);

        /// <summary>Counts one second-factor attempt; false when the source or the account is at its cap.</summary>
        public bool TryAttempt(string source, string account) => TryAdmit(source, account);
    }
}
