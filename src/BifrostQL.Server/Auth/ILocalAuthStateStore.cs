namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// The server-side state local auth keeps beside the app-user table: login sessions, account
    /// lockouts, password history and the authentication audit trail. The app-user table stays the
    /// deployment's own; everything here is Bifrost bookkeeping.
    ///
    /// <para>Production deployments use <see cref="DbLocalAuthStateStore"/>, which provisions its
    /// tables in the same database the app-user rows live in (SQL Server, PostgreSQL, MySQL,
    /// SQLite). <see cref="InMemoryLocalAuthStateStore"/> exists for tests and single-process
    /// development only: its sessions, lockouts and audit trail vanish on restart and are not
    /// shared between nodes.</para>
    ///
    /// <para>No method stores a password, a session id or a cookie. Sessions are addressed by the
    /// SHA-256 of their id (<see cref="LocalSessionId.Hash"/>), password history holds hashes, and
    /// audit events carry only identifiers and a fixed reason vocabulary.</para>
    /// </summary>
    public interface ILocalAuthStateStore
    {
        /// <summary>
        /// Creates whatever storage the store needs. <c>AddBifrostLocalAuth</c> calls it once at host
        /// start, before any request: missing DDL rights fail the start instead of the first login,
        /// and on SQLite no pooled connection can cache a schema that predates the tables (a stale
        /// cache meeting a concurrent writer reports "no such table" instead of waiting).
        /// Idempotent.
        /// </summary>
        Task EnsureProvisionedAsync(CancellationToken cancellationToken);

        /// <summary>Records a newly issued session.</summary>
        Task CreateSessionAsync(LocalSession session, CancellationToken cancellationToken);

        /// <summary>The session with this id hash, revoked or not; null when none was ever issued (or it was purged).</summary>
        Task<LocalSession?> GetSessionAsync(string sessionHash, CancellationToken cancellationToken);

        /// <summary>Moves a live session's last-activity stamp forward. A revoked session is left as it is.</summary>
        Task TouchSessionAsync(string sessionHash, DateTimeOffset lastSeenAt, CancellationToken cancellationToken);

        /// <summary>Revokes one session. False when it was already revoked or does not exist.</summary>
        Task<bool> RevokeSessionAsync(string sessionHash, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken);

        /// <summary>Revokes every unrevoked session the user holds; returns how many were revoked.</summary>
        Task<int> RevokeUserSessionsAsync(string userId, DateTimeOffset revokedAt, string reason, CancellationToken cancellationToken);

        /// <summary>
        /// Moves the user's unrevoked sessions bound to credential stamp <paramref name="fromStamp"/>
        /// onto <paramref name="toStamp"/>; returns how many moved. Called after a login rehashed the
        /// same password at a stronger work factor, so the rehash does not read as a password change
        /// and end the user's other sessions. Sessions bound to any other stamp are left alone.
        /// </summary>
        Task<int> RebindSessionCredentialStampAsync(
            string userId, string fromStamp, string toStamp, CancellationToken cancellationToken);

        /// <summary>
        /// The user's unrevoked sessions whose absolute expiry is after <paramref name="now"/>, oldest
        /// first, at most <paramref name="maxRows"/>.
        /// </summary>
        Task<IReadOnlyList<LocalSession>> ListLiveSessionsAsync(string userId, DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

        /// <summary>Deletes sessions whose absolute expiry is before <paramref name="cutoff"/>.</summary>
        Task PurgeSessionsExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

        /// <summary>The user's lockout state; <see cref="LocalLockoutState.None"/> when no failure is on record.</summary>
        Task<LocalLockoutState> GetLockoutAsync(string userId, CancellationToken cancellationToken);

        /// <summary>
        /// Counts one failed login for the user. When the count of consecutive failures reaches
        /// <paramref name="threshold"/> the account is locked until <paramref name="lockedUntil"/> and
        /// the counter restarts, so the next window after the lock lapses is a full one. A failure
        /// while the account is already locked changes nothing.
        /// </summary>
        Task<LocalFailedLoginResult> RecordFailedLoginAsync(
            string userId, DateTimeOffset at, int threshold, DateTimeOffset lockedUntil, CancellationToken cancellationToken);

        /// <summary>Clears the user's failure count and any lock (successful login, administrator unlock).</summary>
        Task ClearLockoutAsync(string userId, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes the user's lockout row only while it holds a lock that lapsed at or before
        /// <paramref name="now"/>, and returns whether it deleted one. A failure or a new lock recorded
        /// since the caller read the lapsed lock survives, and of two requests that read the same lapsed
        /// lock only one gets true, so the unlock is audited once.
        /// </summary>
        Task<bool> ClearExpiredLockoutAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken);

        /// <summary>The user's most recent password-history entries, newest first, at most <paramref name="maxEntries"/>.</summary>
        Task<IReadOnlyList<LocalPasswordHistoryEntry>> GetPasswordHistoryAsync(string userId, int maxEntries, CancellationToken cancellationToken);

        /// <summary>
        /// Records a password hash the user just set, then drops all but the newest
        /// <paramref name="keep"/> entries.
        /// </summary>
        Task AppendPasswordHistoryAsync(string userId, string passwordHash, DateTimeOffset changedAt, int keep, CancellationToken cancellationToken);

        /// <summary>Appends one authentication audit event. A failure propagates: an unrecorded event is an error, not a warning.</summary>
        Task AppendAuditAsync(AuthAuditEvent auditEvent, CancellationToken cancellationToken);

        /// <summary>Audit events at or after <paramref name="since"/>, oldest first, at most <paramref name="maxRows"/>.</summary>
        Task<IReadOnlyList<AuthAuditEvent>> ReadAuditAsync(DateTimeOffset since, int maxRows, CancellationToken cancellationToken);
    }

    /// <summary>One server-side login session.</summary>
    /// <param name="SessionHash">SHA-256 (lowercase hex) of the session id the cookie carries.</param>
    /// <param name="UserId">The app-user id the session belongs to.</param>
    /// <param name="CreatedAt">Sign-in time.</param>
    /// <param name="LastSeenAt">Last recorded activity (coarsened; see <see cref="LocalAuthOptions.SessionIdleTimeout"/>).</param>
    /// <param name="ExpiresAt">Absolute end: sign-in plus <see cref="LocalAuthOptions.SessionLifetime"/>.</param>
    /// <param name="RevokedAt">When the session was revoked, or null while it is live.</param>
    /// <param name="RevokeReason">One of <see cref="LocalSessionRevokeReasons"/>, or null while live.</param>
    /// <param name="SourceIp">The client IP at sign-in, or null when unknown.</param>
    /// <param name="CredentialStamp">Fingerprint of the password hash at sign-in; a change revokes the session.</param>
    public sealed record LocalSession(
        string SessionHash,
        string UserId,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt,
        string? RevokeReason,
        string? SourceIp,
        string CredentialStamp);

    /// <summary>The fixed vocabulary a session revocation records.</summary>
    public static class LocalSessionRevokeReasons
    {
        public const string Logout = "logout";
        public const string IdleTimeout = "idle-timeout";
        public const string Expired = "expired";
        public const string PasswordChanged = "password-changed";
        public const string PrivilegeChanged = "privilege-changed";
        public const string AccountDisabled = "account-disabled";
        public const string AccountRemoved = "account-removed";
        public const string ConcurrentSessionLimit = "concurrent-session-limit";
        public const string Administrator = "administrator";
    }

    /// <summary>A user's lockout state.</summary>
    /// <param name="FailedCount">Consecutive failed logins since the last success, unlock or lock.</param>
    /// <param name="LockedUntil">End of the current lock, or null when the account is not locked.</param>
    public sealed record LocalLockoutState(int FailedCount, DateTimeOffset? LockedUntil)
    {
        /// <summary>No failure on record.</summary>
        public static LocalLockoutState None { get; } = new(0, null);

        /// <summary>Whether the account is locked at <paramref name="now"/>.</summary>
        public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
    }

    /// <summary>The outcome of <see cref="ILocalAuthStateStore.RecordFailedLoginAsync"/>.</summary>
    /// <param name="State">The state after the failure was counted.</param>
    /// <param name="LockedNow">True when this failure is the one that locked the account.</param>
    public sealed record LocalFailedLoginResult(LocalLockoutState State, bool LockedNow);

    /// <summary>One password the user set, as its stored hash.</summary>
    public sealed record LocalPasswordHistoryEntry(string PasswordHash, DateTimeOffset ChangedAt);
}
