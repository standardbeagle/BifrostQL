namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// The server-side state local auth keeps beside the app-user table: login sessions, account
    /// lockouts, password history, second factors and their login challenges, and the
    /// authentication audit trail. The app-user table stays the
    /// deployment's own; everything here is Bifrost bookkeeping.
    ///
    /// <para>Production deployments use <see cref="DbLocalAuthStateStore"/>, which provisions its
    /// tables in the same database the app-user rows live in (SQL Server, PostgreSQL, MySQL,
    /// SQLite). <see cref="InMemoryLocalAuthStateStore"/> exists for tests and single-process
    /// development only: its sessions, lockouts and audit trail vanish on restart and are not
    /// shared between nodes.</para>
    ///
    /// <para>No method stores a password, a session id, a cookie, a plaintext TOTP secret or a
    /// recovery code. Sessions are addressed by the
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

        /// <summary>Clears the user's failure count and any lock (administrator unlock).</summary>
        Task ClearLockoutAsync(string userId, CancellationToken cancellationToken);

        /// <summary>
        /// After a verified password: clears the user's failure count and any lapsed lock, unless a lock
        /// is in force at <paramref name="now"/>. Returns false, changing nothing, when one is, so a lock
        /// a concurrent failure took after the caller's lockout check is not undone by the success.
        /// </summary>
        Task<bool> ClearFailuresUnlessLockedAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken);

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

        /// <summary>The user's second-factor row, pending or active; null when the user has none.</summary>
        Task<LocalMfaState?> GetMfaAsync(string userId, CancellationToken cancellationToken);

        /// <summary>
        /// Inserts the user's second-factor row at <see cref="LocalMfaState.Version"/> 1. False when the
        /// user already has one (a concurrent enrollment won); the caller re-reads and updates instead.
        /// </summary>
        Task<bool> InsertMfaAsync(LocalMfaState state, CancellationToken cancellationToken);

        /// <summary>
        /// Replaces the user's second-factor row with <paramref name="next"/> only when the stored
        /// version is still <paramref name="expectedVersion"/>, writing version
        /// <paramref name="expectedVersion"/> + 1. False when another write got there first. Every
        /// change to the row (accepted time step, spent recovery code, activation) goes through this
        /// compare-and-swap, so two requests presenting the same code cannot both succeed.
        /// </summary>
        Task<bool> UpdateMfaAsync(LocalMfaState next, int expectedVersion, CancellationToken cancellationToken);

        /// <summary>Deletes the user's second-factor row and its recovery codes; false when there was none.</summary>
        Task<bool> DeleteMfaAsync(string userId, CancellationToken cancellationToken);

        /// <summary>Records a newly issued second-factor login challenge.</summary>
        Task CreateMfaChallengeAsync(LocalMfaChallenge challenge, CancellationToken cancellationToken);

        /// <summary>The challenge with this ticket hash, spent or not; null when none was issued (or it was purged).</summary>
        Task<LocalMfaChallenge?> GetMfaChallengeAsync(string challengeHash, CancellationToken cancellationToken);

        /// <summary>
        /// Replaces a challenge with <paramref name="next"/> only when its stored version is still
        /// <paramref name="expectedVersion"/>, writing version <paramref name="expectedVersion"/> + 1.
        /// False when another write got there first.
        /// </summary>
        Task<bool> UpdateMfaChallengeAsync(LocalMfaChallenge next, int expectedVersion, CancellationToken cancellationToken);

        /// <summary>Deletes challenges whose expiry is before <paramref name="cutoff"/>.</summary>
        Task PurgeMfaChallengesExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
    }

    /// <summary>Whether a user's second factor is awaiting its first code or in force.</summary>
    public enum LocalMfaStatus
    {
        /// <summary>A secret was issued; the user has not yet proved they hold it.</summary>
        Pending,

        /// <summary>Confirmed with a code; every sign-in needs a code or a recovery code.</summary>
        Active,
    }

    /// <summary>
    /// One user's TOTP second factor. The secret is stored only as a field-encryption envelope
    /// bound to the user id; recovery codes only as salted SHA-256 hashes.
    /// </summary>
    /// <param name="UserId">The app-user id.</param>
    /// <param name="SecretEnvelope">The TOTP secret, encrypted (<c>FieldCipher</c> envelope).</param>
    /// <param name="Algorithm">HMAC algorithm the authenticator was set up with.</param>
    /// <param name="Digits">Code length the authenticator was set up with.</param>
    /// <param name="PeriodSeconds">Time step the authenticator was set up with.</param>
    /// <param name="Status">Pending or active.</param>
    /// <param name="CreatedAt">When the secret was issued.</param>
    /// <param name="PendingExpiresAt">When an unconfirmed enrollment lapses; null once active.</param>
    /// <param name="EnabledAt">When the enrollment was confirmed; null while pending.</param>
    /// <param name="LastUsedStep">The newest time step a code was accepted for; a code for this step or an earlier one is a replay.</param>
    /// <param name="RecoveryCodeHashes">Unspent recovery codes as <c>salt.hash</c> hex pairs.</param>
    /// <param name="Version">Optimistic concurrency version.</param>
    public sealed record LocalMfaState(
        string UserId,
        string SecretEnvelope,
        LocalTotpAlgorithm Algorithm,
        int Digits,
        int PeriodSeconds,
        LocalMfaStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? PendingExpiresAt,
        DateTimeOffset? EnabledAt,
        long? LastUsedStep,
        IReadOnlyList<string> RecoveryCodeHashes,
        int Version);

    /// <summary>
    /// A login whose password verified and whose second factor is still owed. The ticket the
    /// client holds is a random 256-bit value; the store keeps only its SHA-256.
    /// </summary>
    /// <param name="ChallengeHash">SHA-256 (lowercase hex) of the ticket.</param>
    /// <param name="UserId">The account whose password verified.</param>
    /// <param name="CredentialStamp">The password fingerprint at that moment; a password change voids the ticket.</param>
    /// <param name="CreatedAt">When the password verified.</param>
    /// <param name="ExpiresAt">When the ticket lapses.</param>
    /// <param name="FailedAttempts">Wrong codes presented with this ticket.</param>
    /// <param name="ConsumedAt">When the ticket was spent (success or too many failures); null while usable.</param>
    /// <param name="Version">Optimistic concurrency version.</param>
    public sealed record LocalMfaChallenge(
        string ChallengeHash,
        string UserId,
        string CredentialStamp,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int FailedAttempts,
        DateTimeOffset? ConsumedAt,
        int Version);

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
    /// <param name="Amr">
    /// How the sign-in was authenticated: space-separated <see cref="LocalAuthMethods"/> values
    /// (<c>pwd</c>, <c>pwd otp</c>, <c>pwd recovery</c>).
    /// </param>
    /// <param name="MfaPending">
    /// True for a session issued to a user who must enroll a second factor and has not: it may reach
    /// only the enrollment and logout endpoints.
    /// </param>
    public sealed record LocalSession(
        string SessionHash,
        string UserId,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt,
        string? RevokeReason,
        string? SourceIp,
        string CredentialStamp,
        string Amr = LocalAuthMethods.Password,
        bool MfaPending = false)
    {
        /// <summary>Whether the sign-in presented a second factor.</summary>
        public bool HasSecondFactor => LocalAuthMethods.HasSecondFactor(Amr);
    }

    /// <summary>
    /// Authentication-method references (RFC 8176 <c>amr</c> values) a local session records and
    /// its cookie carries as repeated <c>amr</c> claims.
    /// </summary>
    public static class LocalAuthMethods
    {
        /// <summary>Password.</summary>
        public const string Password = "pwd";

        /// <summary>Time-based one-time password.</summary>
        public const string Otp = "otp";

        /// <summary>A single-use recovery code (not an RFC 8176 value; Bifrost's own).</summary>
        public const string RecoveryCode = "recovery";

        /// <summary>Whether a space-separated amr list includes a second factor.</summary>
        public static bool HasSecondFactor(string amr) =>
            amr.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(m => m is Otp or RecoveryCode);
    }

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
        public const string MfaChanged = "mfa-changed";
        public const string MfaReset = "mfa-reset";
        public const string MfaRequired = "mfa-required";
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
