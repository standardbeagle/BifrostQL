using System.Data;
using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using Microsoft.AspNetCore.Identity;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Configuration for local DB-backed user login. The app-user table lives in the
    /// same database BifrostQL already serves (resolved through the shared
    /// <see cref="IDbConnFactory"/>); column and table names are configurable so a
    /// deployment can point local auth at whatever schema its app-user rows use.
    /// Database credentials are reached only through the server-side connection
    /// factory and are never exposed to the client.
    /// </summary>
    public sealed class LocalAuthOptions
    {
        /// <summary>Table holding app-user rows. Defaults to <c>app_users</c>.</summary>
        public string UserTable { get; set; } = "app_users";

        /// <summary>Column matched against the submitted login name. Defaults to <c>email</c>.</summary>
        public string LoginColumn { get; set; } = "email";

        /// <summary>Stable user identifier column. Defaults to <c>id</c>.</summary>
        public string IdColumn { get; set; } = "id";

        /// <summary>Column holding the hashed password. Defaults to <c>password_hash</c>.</summary>
        public string PasswordHashColumn { get; set; } = "password_hash";

        /// <summary>Optional display-name column. Defaults to <c>display_name</c>.</summary>
        public string DisplayNameColumn { get; set; } = "display_name";

        /// <summary>Optional tenant identifier column. Defaults to <c>tenant_id</c>.</summary>
        public string TenantColumn { get; set; } = "tenant_id";

        /// <summary>
        /// Optional column holding a delimited role list (e.g. <c>admin,editor</c>).
        /// Defaults to <c>roles</c>.
        /// </summary>
        public string RolesColumn { get; set; } = "roles";

        /// <summary>
        /// Optional table holding member-profile rows, used to enrich the login
        /// identity with a <c>household_id</c> claim. Member-claim enrichment is
        /// opt-in: it runs only when <see cref="MemberTable"/>,
        /// <see cref="MemberUserIdColumn"/>, and <see cref="MemberHouseholdColumn"/>
        /// are all set. Null by default.
        /// </summary>
        public string? MemberTable { get; set; }

        /// <summary>
        /// Column on <see cref="MemberTable"/> holding the FK back to the app-user
        /// id (<see cref="IdColumn"/>). Used to resolve the caller's member row
        /// during login. Null by default.
        /// </summary>
        public string? MemberUserIdColumn { get; set; }

        /// <summary>
        /// Column on <see cref="MemberTable"/> holding the household identifier.
        /// When the resolved member row carries a non-null value, it is surfaced
        /// as the <c>household_id</c> provider claim so the households policy
        /// row-scope resolves for the caller. Null by default.
        /// </summary>
        public string? MemberHouseholdColumn { get; set; }

        /// <summary>Login path for the local auth endpoint. Defaults to <c>/auth/login</c>.</summary>
        public string LoginPath { get; set; } = "/auth/login";

        /// <summary>Logout path for the local auth endpoint. Defaults to <c>/auth/logout</c>.</summary>
        public string LogoutPath { get; set; } = "/auth/logout";

        /// <summary>Read-session path for the local auth endpoint. Defaults to <c>/auth/session</c>.</summary>
        public string SessionPath { get; set; } = "/auth/session";

        /// <summary>
        /// Login attempts one client IP may make within <see cref="LoginAttemptWindow"/>, across
        /// every login it names, before the endpoint answers 429 without verifying. Every attempt
        /// counts (it is reserved before the password hash runs). Must be at least 1. Defaults to 20.
        /// </summary>
        public int MaxLoginAttemptsPerSource { get; set; } = 20;

        /// <summary>
        /// Login attempts one login may receive within <see cref="LoginAttemptWindow"/>, from every
        /// source IP combined, before the endpoint answers 429 without verifying. Bounds guessing
        /// spread over many IPs; the cost is that a flood against one login also holds its owner
        /// out until the window rolls over. Must be at least 1. Defaults to 10.
        /// </summary>
        public int MaxLoginAttemptsPerAccount { get; set; } = 10;

        /// <summary>
        /// Fixed window over which both login-attempt caps are counted. Defaults to 5 minutes.
        /// </summary>
        public TimeSpan LoginAttemptWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Absolute lifetime of a login session, counted from sign-in. Activity does not extend
        /// it. Defaults to 8 hours.
        /// </summary>
        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

        /// <summary>When a session issued at <paramref name="now"/> expires.</summary>
        internal DateTimeOffset SessionEndsAt(DateTimeOffset now) => now + SessionLifetime;

        /// <summary>
        /// Longest accepted <see cref="SessionLifetime"/> and <see cref="LockoutDuration"/>: 3650 days.
        /// The runtime adds both to the current time (<see cref="SessionEndsAt"/>,
        /// <see cref="LockoutEndsAt"/>, the cookie's expiry), and a larger value would overflow
        /// <see cref="DateTimeOffset"/> at the first sign-in or lock instead of failing at startup.
        /// </summary>
        public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(3650);

        /// <summary>
        /// A session with no request for this long is revoked (PCI DSS 8.2.8 asks for 15 minutes
        /// or less). Activity is recorded at most once per <c>min(1 minute, SessionIdleTimeout / 15)</c>,
        /// so a session can outlive the timeout by that much. Must be positive and no longer than
        /// <see cref="SessionLifetime"/>. Defaults to 15 minutes.
        /// </summary>
        public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How often an issued session re-reads its user row. A deleted or disabled user, a
        /// changed password, or a changed tenant, role list or household revokes every session
        /// of that user at the first request after this interval. <see cref="TimeSpan.Zero"/>
        /// re-reads on every request. Defaults to 1 minute.
        /// </summary>
        public TimeSpan SessionRevalidationInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Most live sessions one user may hold. A sign-in past the cap revokes that user's oldest
        /// sessions. Null (the default) is no cap; otherwise at least 1.
        /// </summary>
        public int? MaxConcurrentSessions { get; set; }

        /// <summary>
        /// Optional column on <see cref="UserTable"/> marking an account disabled (NULL, false or 0
        /// is enabled). A disabled account cannot sign in, and its live sessions are revoked at the
        /// next revalidation. Null (the default) reads no such column.
        /// </summary>
        public string? DisabledColumn { get; set; }

        /// <summary>
        /// Role that may call the session and lockout administration endpoints under
        /// <see cref="AdminPath"/>. Null (the default) maps no administration endpoint;
        /// <see cref="LocalAuthAdministration"/> stays available to host code either way.
        /// </summary>
        public string? AdminRole { get; set; }

        /// <summary>Base path of the administration endpoints. Defaults to <c>/auth/admin</c>.</summary>
        public string AdminPath { get; set; } = "/auth/admin";

        /// <summary>
        /// Consecutive failed logins against one account that lock it (PCI DSS 8.3.4 asks for at
        /// most 10). Counted per account across every source, beside and separate from the
        /// <see cref="MaxLoginAttemptsPerAccount"/> rate window. A success or an unlock resets it.
        /// Defaults to 10; at least 1.
        /// </summary>
        public int LockoutThreshold { get; set; } = 10;

        /// <summary>
        /// How long a locked account stays locked (PCI DSS 8.3.4 asks for at least 30 minutes or
        /// until an administrator unlocks it). Null locks until <see cref="LocalAuthAdministration.UnlockAccountAsync"/>.
        /// Defaults to 30 minutes.
        /// </summary>
        public TimeSpan? LockoutDuration { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>When a lock taken at <paramref name="now"/> ends; <see cref="DateTimeOffset.MaxValue"/> when only an administrator ends it.</summary>
        internal DateTimeOffset LockoutEndsAt(DateTimeOffset now) =>
            LockoutDuration is { } duration ? now + duration : DateTimeOffset.MaxValue;

        /// <summary>Password-change path for the local auth endpoint. Defaults to <c>/auth/password</c>.</summary>
        public string PasswordChangePath { get; set; } = "/auth/password";

        /// <summary>
        /// Fewest characters (Unicode scalar values) a new password may have (PCI DSS 8.3.6 asks for
        /// 12). Applies to passwords set through <see cref="PasswordChangePath"/>. Defaults to 12.
        /// </summary>
        public int MinPasswordLength { get; set; } = 12;

        /// <summary>
        /// A new password must contain at least one letter and one digit (PCI DSS 8.3.6). Defaults to
        /// true; set false where a length-only policy (NIST SP 800-63B) governs instead.
        /// </summary>
        public bool PasswordRequiresLetterAndDigit { get; set; } = true;

        /// <summary>Most characters a new password may have. Defaults to 256; at least <see cref="MinPasswordLength"/>.</summary>
        public int MaxPasswordLength { get; set; } = 256;

        /// <summary>
        /// A new password may not equal the current one or any of this many passwords before it
        /// (PCI DSS 8.3.7 asks for 4). The store keeps the current hash plus that many per user. 0
        /// checks only the current password. Defaults to 4; at most 24.
        /// </summary>
        public int PasswordHistoryCount { get; set; } = 4;

        /// <summary>History entries kept and checked per user: the current password plus <see cref="PasswordHistoryCount"/> before it.</summary>
        internal int PasswordHistoryEntriesKept => PasswordHistoryCount + 1;

        /// <summary>
        /// Longest a password stays valid after it was set through <see cref="PasswordChangePath"/>
        /// (PCI DSS 8.3.9 rotation). A sign-in past it answers 403 <c>password_expired</c> and issues
        /// no session; the password-change endpoint still accepts it. An account whose password was
        /// never changed through the endpoint has no recorded change date and counts as expired.
        /// Null (the default) never expires a password.
        /// </summary>
        public TimeSpan? MaxPasswordAge { get; set; }

        /// <summary>
        /// PBKDF2 iteration count for password hashes this host writes (ASP.NET Core Identity V3:
        /// PBKDF2-HMAC-SHA512, 128-bit salt, 256-bit subkey). A stored hash below the count still
        /// verifies and is rehashed at the configured count on the next successful login.
        /// Defaults to <see cref="DefaultPasswordHashIterations"/> (OWASP's figure for
        /// PBKDF2-HMAC-SHA512); must be at least <see cref="MinPasswordHashIterations"/>, the same
        /// figure, so configuration can only strengthen it. Tests that need cheap hashing pass their
        /// own <see cref="IPasswordHasher{TUser}"/> to <see cref="LocalUserStore"/> instead.
        /// </summary>
        public int PasswordHashIterations { get; set; } = DefaultPasswordHashIterations;

        /// <summary>Default <see cref="PasswordHashIterations"/>: 210,000.</summary>
        public const int DefaultPasswordHashIterations = 210_000;

        /// <summary>
        /// Lowest accepted <see cref="PasswordHashIterations"/>: 210,000, the OWASP Password Storage
        /// Cheat Sheet figure for PBKDF2-HMAC-SHA512. The ASP.NET Core default (100,000) is below it.
        /// </summary>
        public const int MinPasswordHashIterations = DefaultPasswordHashIterations;

        /// <summary>
        /// Every local sign-in needs a second factor (PCI DSS 4.0 8.4.2, HIPAA 164.312(d)). An enrolled
        /// user's password answers <c>mfa-required</c> with a challenge ticket instead of a session; a
        /// user who has not enrolled gets an enrollment-only session (<c>mfa-enrollment-required</c>)
        /// that reaches nothing but <see cref="MfaPath"/><c>/enroll</c>, <c>/enroll/confirm</c> and
        /// logout. Needs <see cref="MfaKeyRef"/>. Defaults to true: set false to let users who have not
        /// enrolled sign in with a password alone (enrolled users still need their factor).
        /// </summary>
        public bool RequireMfa { get; set; } = true;

        /// <summary>
        /// Roles whose members need a second factor when <see cref="RequireMfa"/> is false (for example
        /// only the administrators who reach the cardholder data environment). Needs
        /// <see cref="MfaKeyRef"/> when not empty. Empty by default.
        /// </summary>
        public IList<string> RequireMfaForRoles { get; set; } = new List<string>();

        /// <summary>
        /// Field-encryption key-ref (<c>provider:id</c>, e.g. <c>config:mfa</c>) whose data key encrypts
        /// TOTP secrets at rest. The host registers an <c>IRootKeyProvider</c> and a durable
        /// <c>IDataEncryptionKeyStore</c>; a missing one fails the host start. Null (the default) turns
        /// MFA off: no MFA endpoint is mapped, and a user who enrolled earlier cannot sign in until it
        /// is set again (403 <c>mfa_unavailable</c>), so removing it never skips a factor.
        /// </summary>
        public string? MfaKeyRef { get; set; }

        /// <summary>Base path of the MFA endpoints. Defaults to <c>/auth/mfa</c>.</summary>
        public string MfaPath { get; set; } = "/auth/mfa";

        /// <summary>Issuer shown by authenticator apps (the <c>otpauth://</c> label and <c>issuer</c>). Defaults to <c>BifrostQL</c>.</summary>
        public string MfaIssuer { get; set; } = "BifrostQL";

        /// <summary>HMAC of new enrollments. Defaults to SHA-1, the only one every authenticator app reads.</summary>
        public LocalTotpAlgorithm MfaTotpAlgorithm { get; set; } = LocalTotpAlgorithm.Sha1;

        /// <summary>Code length of new enrollments, 6 to 8. Defaults to 6.</summary>
        public int MfaTotpDigits { get; set; } = 6;

        /// <summary>Time step of new enrollments in seconds, 15 to 300. Defaults to 30.</summary>
        public int MfaTotpPeriodSeconds { get; set; } = 30;

        /// <summary>How long a challenge ticket from a verified password stays usable. Defaults to 5 minutes; at most 15.</summary>
        public TimeSpan MfaChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long an unconfirmed enrollment, and an enrollment-only session, lasts. Defaults to 10
        /// minutes.
        /// </summary>
        public TimeSpan MfaEnrollmentTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Wrong codes one challenge ticket accepts before it is spent. Defaults to 5; at least 1.</summary>
        public int MaxMfaChallengeAttempts { get; set; } = 5;

        /// <summary>
        /// Second-factor attempts (login step two, enrollment confirm, disable, regenerate) one client
        /// IP may make within <see cref="LoginAttemptWindow"/> before 429. Counted before the code is
        /// checked. Defaults to 20; at least 1.
        /// </summary>
        public int MaxMfaAttemptsPerSource { get; set; } = 20;

        /// <summary>
        /// Second-factor attempts one account may receive within <see cref="LoginAttemptWindow"/>, from
        /// every source combined, before 429. Wrong codes also count toward <see cref="LockoutThreshold"/>.
        /// Defaults to 10; at least 1.
        /// </summary>
        public int MaxMfaAttemptsPerAccount { get; set; } = 10;

        /// <summary>Recovery codes issued at enrollment and at each regeneration.</summary>
        public const int RecoveryCodeCount = 10;

        /// <summary>Whether MFA is configured at all (a key-ref to encrypt secrets under).</summary>
        internal bool MfaEnabled => MfaKeyRef is not null;

        /// <summary>Whether a user holding <paramref name="roles"/> must present a second factor.</summary>
        internal bool MfaRequiredFor(IEnumerable<string> roles) =>
            RequireMfa || (RequireMfaForRoles.Count > 0 && roles.Any(r => RequireMfaForRoles.Contains(r, StringComparer.Ordinal)));

        /// <summary>
        /// Throws on a setting no login could honour. <c>AddBifrostLocalAuth</c> calls it at startup;
        /// the runtime reads the same properties, so there is no second parser to drift.
        /// </summary>
        public void Validate()
        {
            static void Require(bool ok, string name, object? value, string rule)
            {
                if (!ok)
                    throw new ArgumentOutOfRangeException(name, value, $"LocalAuthOptions.{name} {rule}.");
            }

            Require(SessionLifetime > TimeSpan.Zero && SessionLifetime <= MaxDuration, nameof(SessionLifetime),
                SessionLifetime, "must be positive and at most 3650 days");
            Require(SessionIdleTimeout > TimeSpan.Zero && SessionIdleTimeout <= SessionLifetime,
                nameof(SessionIdleTimeout), SessionIdleTimeout, "must be positive and no longer than SessionLifetime");
            Require(SessionRevalidationInterval >= TimeSpan.Zero, nameof(SessionRevalidationInterval),
                SessionRevalidationInterval, "must not be negative");
            Require(MaxConcurrentSessions is null or >= 1, nameof(MaxConcurrentSessions), MaxConcurrentSessions,
                "must be null or at least 1");
            Require(AdminRole is null || !string.IsNullOrWhiteSpace(AdminRole), nameof(AdminRole), AdminRole,
                "must be null or a role name");
            Require(LockoutThreshold >= 1, nameof(LockoutThreshold), LockoutThreshold, "must be at least 1");
            Require(LockoutDuration is null || (LockoutDuration > TimeSpan.Zero && LockoutDuration <= MaxDuration),
                nameof(LockoutDuration), LockoutDuration, "must be null, or positive and at most 3650 days");
            Require(MinPasswordLength >= 1, nameof(MinPasswordLength), MinPasswordLength, "must be at least 1");
            Require(MaxPasswordLength >= MinPasswordLength, nameof(MaxPasswordLength), MaxPasswordLength,
                "must be at least MinPasswordLength");
            Require(PasswordHistoryCount is >= 0 and <= 24, nameof(PasswordHistoryCount), PasswordHistoryCount,
                "must be between 0 and 24");
            Require(MaxPasswordAge is null || MaxPasswordAge > TimeSpan.Zero, nameof(MaxPasswordAge), MaxPasswordAge,
                "must be null or positive");
            _ = CreatePasswordHasher();

            Require(MfaKeyRef is null || BifrostQL.Core.Crypto.KeyRef.TryParse(MfaKeyRef, out _), nameof(MfaKeyRef), MfaKeyRef,
                "must be null or a key-ref of the form provider:id with provider kms or config");
            Require(MfaTotpDigits is >= 6 and <= 8, nameof(MfaTotpDigits), MfaTotpDigits, "must be between 6 and 8");
            Require(MfaTotpPeriodSeconds is >= 15 and <= 300, nameof(MfaTotpPeriodSeconds), MfaTotpPeriodSeconds,
                "must be between 15 and 300");
            Require(MfaChallengeLifetime > TimeSpan.Zero && MfaChallengeLifetime <= TimeSpan.FromMinutes(15),
                nameof(MfaChallengeLifetime), MfaChallengeLifetime, "must be positive and at most 15 minutes");
            Require(MfaEnrollmentTimeout > TimeSpan.Zero && MfaEnrollmentTimeout <= MaxDuration, nameof(MfaEnrollmentTimeout),
                MfaEnrollmentTimeout, "must be positive and at most 3650 days");
            Require(MaxMfaChallengeAttempts >= 1, nameof(MaxMfaChallengeAttempts), MaxMfaChallengeAttempts, "must be at least 1");
            Require(!string.IsNullOrWhiteSpace(MfaIssuer) && !MfaIssuer.Contains(':'), nameof(MfaIssuer), MfaIssuer,
                "must be a name without a colon");
            if ((RequireMfa || RequireMfaForRoles.Count > 0) && MfaKeyRef is null)
                throw new InvalidOperationException(
                    "LocalAuthOptions requires a second factor (RequireMfa defaults to true) but MfaKeyRef is not set. " +
                    "Set MfaKeyRef to a field-encryption key-ref such as \"config:mfa\" and register an IRootKeyProvider " +
                    "and a durable IDataEncryptionKeyStore, or set RequireMfa = false to let users who have not " +
                    "enrolled sign in with a password alone.");
        }

        /// <summary>How often a live session's activity stamp is written: min(1 minute, idle timeout / 15).</summary>
        internal TimeSpan SessionTouchInterval =>
            TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, SessionIdleTimeout.Ticks / 15));

        /// <summary>
        /// The password hasher these options configure. Throws when
        /// <see cref="PasswordHashIterations"/> is below <see cref="MinPasswordHashIterations"/>;
        /// <see cref="Validate"/> calls it at startup so a weak setting never reaches a login.
        /// </summary>
        public IPasswordHasher<string> CreatePasswordHasher()
        {
            if (PasswordHashIterations < MinPasswordHashIterations)
                throw new ArgumentOutOfRangeException(nameof(PasswordHashIterations), PasswordHashIterations,
                    $"LocalAuthOptions.PasswordHashIterations must be at least {MinPasswordHashIterations}.");
            return new PasswordHasher<string>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                IterationCount = PasswordHashIterations,
            }));
        }
    }

    /// <summary>
    /// Result of a credential verification attempt. <see cref="Succeeded"/> is true only
    /// when the user exists and the submitted password matches the stored hash; the
    /// <see cref="Identity"/> is populated only on success.
    /// </summary>
    public sealed record LocalLoginResult
    {
        private LocalLoginResult(bool succeeded, AppIdentity? identity, LocalAccountState? account, string? rehashFrom = null)
        {
            Succeeded = succeeded;
            Identity = identity;
            Account = account;
            RehashFrom = rehashFrom;
        }

        /// <summary>Whether the credentials were valid and the account may sign in.</summary>
        public bool Succeeded { get; }

        /// <summary>The authenticated identity, or <c>null</c> when verification failed.</summary>
        public AppIdentity? Identity { get; }

        /// <summary>
        /// The account the login named, when one matched, whether or not the password did. Server-side
        /// only: lockout counts failures against <see cref="LocalAccountState.UserId"/>. Never put it on
        /// the wire, where it would reveal that the account exists.
        /// </summary>
        public LocalAccountState? Account { get; }

        /// <summary>
        /// The stored hash that verified, when it is in an older format or below the current work
        /// factor; null otherwise. Server-side only: <see cref="LocalUserStore.UpgradePasswordHashAsync"/>
        /// replaces it once the caller has passed its lockout check.
        /// </summary>
        internal string? RehashFrom { get; }

        /// <summary>A successful result carrying the resolved identity.</summary>
        public static LocalLoginResult Success(AppIdentity identity) => new(true, identity, null);

        internal static LocalLoginResult Success(AppIdentity identity, LocalAccountState account, string? rehashFrom) =>
            new(true, identity, account, rehashFrom);

        /// <summary>
        /// A failed result. Used for both "no such user" and "wrong password" so the
        /// caller cannot distinguish the two and leak account existence.
        /// </summary>
        public static LocalLoginResult Failure() => new(false, null, null);

        internal static LocalLoginResult Failure(LocalAccountState? account) => new(false, null, account);
    }

    /// <summary>
    /// Server-side facts about one app-user row that session and lockout decisions need.
    /// </summary>
    /// <param name="UserId">The stable user id.</param>
    /// <param name="CredentialStamp">Fingerprint of the stored password hash (never the hash itself).</param>
    /// <param name="Disabled">True when <see cref="LocalAuthOptions.DisabledColumn"/> marks the account disabled.</param>
    public sealed record LocalAccountState(string UserId, string CredentialStamp, bool Disabled);

    /// <summary>An app-user's identity plus the account facts a session revalidates against.</summary>
    public sealed record LocalAccount(AppIdentity Identity, LocalAccountState State);

    /// <summary>
    /// Reads app-user rows from the BifrostQL database and verifies submitted passwords
    /// against the stored hash. Password hashes are verified server-side with the vetted
    /// ASP.NET Core <see cref="PasswordHasher{TUser}"/>; no plaintext password is ever
    /// stored or compared. On success the store produces the same provider-agnostic
    /// <see cref="AppIdentity"/> contract every other authentication path produces.
    /// </summary>
    public sealed class LocalUserStore
    {
        private readonly IDbConnFactory _connFactory;
        private readonly LocalAuthOptions _options;
        private readonly IPasswordHasher<string> _passwordHasher;

        /// <summary>
        /// A precomputed hash used only to spend the same PBKDF2 work on a user-miss as a
        /// real verification does. Without it, a missing user returns before any
        /// <see cref="IPasswordHasher{TUser}.VerifyHashedPassword"/> call, so the ~100 ms
        /// hashing cost becomes a timing oracle that distinguishes "no such user" from
        /// "wrong password". Verifying the submitted password against this throwaway hash
        /// keeps the miss and wrong-password paths on the same code/timing path.
        /// </summary>
        private readonly string _dummyHash;

        /// <summary>
        /// The PBKDF2 iteration count of <see cref="_dummyHash"/>: the work a user-miss spends. A real
        /// verification against a weaker stored hash (an older Identity format or a lower count) is
        /// topped up to it through <see cref="_padWork"/>, so a miss and a legacy account cost the same.
        /// Null when the hasher's output is not an Identity hash and the count cannot be read.
        /// </summary>
        private readonly int? _decoyIterations;

        /// <summary>Spends the given number of PBKDF2 iterations. Injected by tests to count work.</summary>
        private readonly Action<int> _padWork;

        /// <summary>
        /// Creates a store over the shared connection factory.
        /// </summary>
        /// <param name="connFactory">
        /// Server-side database connection factory. The connection string it holds never
        /// reaches the client.
        /// </param>
        /// <param name="options">Table and column configuration for the app-user rows.</param>
        /// <param name="passwordHasher">
        /// Password hasher used to verify the submitted password against the stored hash.
        /// Defaults to <see cref="LocalAuthOptions.CreatePasswordHasher"/> when not supplied.
        /// </param>
        public LocalUserStore(
            IDbConnFactory connFactory,
            LocalAuthOptions options,
            IPasswordHasher<string>? passwordHasher = null)
            : this(connFactory, options, passwordHasher, SpendPbkdf2Iterations)
        {
        }

        internal LocalUserStore(
            IDbConnFactory connFactory,
            LocalAuthOptions options,
            IPasswordHasher<string>? passwordHasher,
            Action<int> padWork)
        {
            _padWork = padWork ?? throw new ArgumentNullException(nameof(padWork));
            _connFactory = connFactory ?? throw new ArgumentNullException(nameof(connFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _passwordHasher = passwordHasher ?? options.CreatePasswordHasher();
            // Compute the throwaway hash once so a user-miss can verify against it in
            // constant-ish time relative to a real verification (see _dummyHash).
            _dummyHash = _passwordHasher.HashPassword(DummyUser, DummyPassword);
            _decoyIterations = IdentityHashIterations(_dummyHash);
        }

        /// <summary>
        /// The PBKDF2 iteration count an ASP.NET Core Identity hash embeds: V3 (marker 0x01) stores it
        /// big-endian at bytes 5-8, V2 (marker 0x00) is fixed at 1,000. Null for any other shape.
        /// </summary>
        internal static int? IdentityHashIterations(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            var bytes = new byte[hash.Length];
            if (!Convert.TryFromBase64String(hash, bytes, out var length) || length == 0) return null;
            return bytes[0] switch
            {
                0x00 => 1_000,
                0x01 when length >= 13 => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4)),
                _ => null,
            };
        }

        private static readonly byte[] PadSalt = new byte[16];

        private static void SpendPbkdf2Iterations(int iterations) =>
            System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                DummyPassword, PadSalt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA512, 32);

        /// <summary>
        /// Tops a real verification up to the decoy's work, so an account whose stored hash is weaker
        /// than the decoy (legacy format, lower count) answers in the time a missing account does.
        /// </summary>
        private void PadToDecoyWork(string storedHash)
        {
            if (_decoyIterations is not { } decoy || IdentityHashIterations(storedHash) is not { } stored) return;
            if (stored < decoy) _padWork(decoy - stored);
        }

        /// <summary>The <see cref="AppIdentity.Provider"/> every local-auth identity carries.</summary>
        public const string ProviderName = "local";

        private const string DummyUser = " bifrost-timing-guard";
        private const string DummyPassword = " bifrost-timing-guard-password";

        /// <summary>
        /// Spends a verification against the throwaway hash so a user-miss follows the same
        /// hashing code path (and cost) as a real password check. The result is discarded.
        /// </summary>
        private void SpendDummyVerification(string login, string password)
            => _passwordHasher.VerifyHashedPassword(login, _dummyHash, password);

        /// <summary>
        /// Verifies <paramref name="login"/> / <paramref name="password"/> against the
        /// app-user table. Returns <see cref="LocalLoginResult.Failure"/> for a missing
        /// user, a blank credential, or a password that does not match the stored hash;
        /// returns <see cref="LocalLoginResult.Success"/> with a populated
        /// <see cref="AppIdentity"/> otherwise.
        /// </summary>
        public async Task<LocalLoginResult> VerifyCredentialsAsync(
            string login,
            string password,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
                return LocalLoginResult.Failure();

            var dialect = _connFactory.Dialect;
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var row = await ReadUserRowAsync(conn, dialect, _options.LoginColumn, login, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.PasswordHash))
            {
                // No such user (or an unusable row): still spend the hashing cost so the
                // response time does not reveal that the account is absent (timing-oracle defense).
                SpendDummyVerification(login, password);
                return LocalLoginResult.Failure();
            }

            var account = StateOf(row);
            var verification = _passwordHasher.VerifyHashedPassword(login, row.PasswordHash, password);
            PadToDecoyWork(row.PasswordHash);
            if (verification == PasswordVerificationResult.Failed || account.Disabled)
                return LocalLoginResult.Failure(account);

            // A hash in an older format or below the current work factor verified. It is not
            // rewritten here: the caller upgrades it (UpgradePasswordHashAsync) only after its
            // lockout check, so a login never rewrites a locked account's row.
            return LocalLoginResult.Success(
                await BuildIdentityAsync(conn, dialect, row, cancellationToken).ConfigureAwait(false), account,
                verification == PasswordVerificationResult.SuccessRehashNeeded ? row.PasswordHash : null);
        }

        /// <summary>
        /// Rehashes a weak stored hash at the configured work factor once <paramref name="result"/>
        /// verified it (<see cref="LocalLoginResult.RehashFrom"/>), and returns the account facts a new
        /// session binds to. The write is conditioned on the hash that verified: a password change that
        /// landed in between wins and the upgrade is dropped (retried at the next login). Returns the
        /// account unchanged when no rehash is due or the write lost that race.
        /// </summary>
        internal async Task<LocalAccountState> UpgradePasswordHashAsync(
            LocalLoginResult result, string login, string password, CancellationToken cancellationToken)
        {
            var account = result.Account ?? throw new ArgumentException("Only a verified result can be upgraded.", nameof(result));
            if (!result.Succeeded || result.RehashFrom is not { } verifiedHash)
                return account;

            var upgraded = _passwordHasher.HashPassword(login, password);
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await StorePasswordHashAsync(conn, _connFactory.Dialect, account.UserId, upgraded, verifiedHash, cancellationToken)
                    .ConfigureAwait(false)
                ? account with { CredentialStamp = LocalSessionId.CredentialStamp(upgraded) }
                : account;
        }

        /// <summary>
        /// Re-reads the user by its stable id: the identity a fresh login would produce plus the
        /// account facts (credential stamp, disabled flag) a live session is checked against.
        /// Null when the row is gone.
        /// </summary>
        public async Task<LocalAccount?> FindAccountByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var dialect = _connFactory.Dialect;
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var row = await ReadUserRowAsync(conn, dialect, _options.IdColumn, id, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.Login)
                || string.IsNullOrEmpty(row.PasswordHash))
                return null;

            var identity = await BuildIdentityAsync(conn, dialect, row, cancellationToken).ConfigureAwait(false);
            return new LocalAccount(identity, StateOf(row));
        }

        private static LocalAccountState StateOf(UserRow row) =>
            new(row.Id!, LocalSessionId.CredentialStamp(row.PasswordHash!), row.Disabled);

        /// <summary>
        /// Re-reads the user by its stable id and rebuilds the identity a fresh login would
        /// produce (tenant, roles, household), without a password. Null when the row is gone.
        /// The cookie session revalidates against this, so a role change, tenant move or
        /// deleted row reaches an already-issued session within
        /// <see cref="LocalAuthOptions.SessionRevalidationInterval"/>.
        /// </summary>
        public async Task<AppIdentity?> FindIdentityByIdAsync(string id, CancellationToken cancellationToken = default)
            => (await FindAccountByIdAsync(id, cancellationToken).ConfigureAwait(false))?.Identity;

        private sealed record UserRow(
            string? Id, string? PasswordHash, string? DisplayName, string? TenantId, string? Roles, string? Login,
            bool Disabled);

        private async Task<UserRow?> ReadUserRowAsync(
            DbConnection conn, ISqlDialect dialect, string keyColumn, string keyValue, CancellationToken cancellationToken)
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                $"SELECT {dialect.EscapeIdentifier(_options.IdColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.PasswordHashColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.DisplayNameColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.TenantColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.RolesColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.LoginColumn)}" +
                (_options.DisabledColumn is { } disabled ? $", {dialect.EscapeIdentifier(disabled)} " : " ") +
                $"FROM {dialect.EscapeIdentifier(_options.UserTable)} " +
                $"WHERE {dialect.EscapeIdentifier(keyColumn)} = @key";

            var keyParam = command.CreateParameter();
            keyParam.ParameterName = "@key";
            keyParam.DbType = DbType.String;
            keyParam.Value = keyValue;
            command.Parameters.Add(keyParam);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            return new UserRow(
                GetString(reader, 0), GetString(reader, 1), GetString(reader, 2),
                GetString(reader, 3), GetString(reader, 4), GetString(reader, 5),
                _options.DisabledColumn is not null && ReadDisabled(reader, 6));
        }

        /// <summary>
        /// Reads the disabled flag. NULL is enabled; a boolean, an integer (non-zero = disabled) or the
        /// text <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c> is accepted. Anything else is a schema fault
        /// and throws rather than guessing which way an ambiguous value should fail.
        /// </summary>
        private static bool ReadDisabled(DbDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
                return false;
            return reader.GetValue(ordinal) switch
            {
                bool flag => flag,
                byte or short or int or long or sbyte or ushort or uint or ulong or decimal =>
                    Convert.ToDecimal(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) != 0,
                string text when text.Trim() is "1" || text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) => true,
                string text when text.Trim() is "0" || text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) => false,
                var other => throw new InvalidOperationException(
                    $"The disabled column holds a {other.GetType().Name} value that is not a recognised flag."),
            };
        }

        /// <summary>
        /// Hashes <paramref name="newPassword"/> at the configured work factor, stores it on the user
        /// row, and returns the stored hash (for the password history).
        /// </summary>
        internal async Task<string> SetPasswordAsync(string userId, string login, string newPassword, CancellationToken cancellationToken)
        {
            var hash = _passwordHasher.HashPassword(login, newPassword);
            var dialect = _connFactory.Dialect;
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await StorePasswordHashAsync(conn, dialect, userId, hash, expectedHash: null, cancellationToken).ConfigureAwait(false);
            return hash;
        }

        /// <summary>Whether <paramref name="password"/> is the plaintext of <paramref name="hash"/>.</summary>
        internal bool PasswordMatchesHash(string login, string hash, string password) =>
            _passwordHasher.VerifyHashedPassword(login, hash, password) != PasswordVerificationResult.Failed;

        /// <summary>
        /// Writes <paramref name="hash"/> to the user row. With <paramref name="expectedHash"/> the write
        /// lands only while the row still holds that hash (compare-and-set); returns whether a row changed.
        /// </summary>
        private async Task<bool> StorePasswordHashAsync(
            DbConnection conn, ISqlDialect dialect, string id, string hash, string? expectedHash,
            CancellationToken cancellationToken)
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                $"UPDATE {dialect.EscapeIdentifier(_options.UserTable)} " +
                $"SET {dialect.EscapeIdentifier(_options.PasswordHashColumn)} = @hash " +
                $"WHERE {dialect.EscapeIdentifier(_options.IdColumn)} = @id" +
                (expectedHash is null ? "" : $" AND {dialect.EscapeIdentifier(_options.PasswordHashColumn)} = @expectedHash");

            var hashParam = command.CreateParameter();
            hashParam.ParameterName = "@hash";
            hashParam.DbType = DbType.String;
            hashParam.Value = hash;
            command.Parameters.Add(hashParam);

            var idParam = command.CreateParameter();
            idParam.ParameterName = "@id";
            idParam.DbType = DbType.String;
            idParam.Value = id;
            command.Parameters.Add(idParam);

            if (expectedHash is not null)
            {
                var expectedParam = command.CreateParameter();
                expectedParam.ParameterName = "@expectedHash";
                expectedParam.DbType = DbType.String;
                expectedParam.Value = expectedHash;
                command.Parameters.Add(expectedParam);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

        private async Task<AppIdentity> BuildIdentityAsync(
            DbConnection conn, ISqlDialect dialect, UserRow row, CancellationToken cancellationToken)
        {
            var claims = await ResolveMemberClaimsAsync(conn, dialect, row.Id!, cancellationToken)
                .ConfigureAwait(false);

            return new AppIdentity(
                id: row.Id!,
                provider: ProviderName,
                // The row's own spelling: a case- or accent-insensitive login column matches any
                // spelling the caller typed, and the claim must not carry that one.
                email: row.Login,
                displayName: string.IsNullOrWhiteSpace(row.DisplayName) ? null : row.DisplayName,
                tenantId: string.IsNullOrWhiteSpace(row.TenantId) ? null : row.TenantId,
                roles: ParseRoles(row.Roles),
                claims: claims);
        }

        /// <summary>
        /// Resolves the authenticated user's member row and surfaces its
        /// <c>household_id</c> as a provider claim, so the households policy
        /// row-scope (<c>household_id = {household_id}</c>) resolves for the
        /// caller. Returns no claims when member enrichment is not configured, no
        /// member row is linked to the user, or the member has no household.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, object?>?> ResolveMemberClaimsAsync(
            DbConnection conn,
            ISqlDialect dialect,
            string userId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.MemberTable)
                || string.IsNullOrWhiteSpace(_options.MemberUserIdColumn)
                || string.IsNullOrWhiteSpace(_options.MemberHouseholdColumn))
                return null;

            await using var command = conn.CreateCommand();
            command.CommandText =
                $"SELECT {dialect.EscapeIdentifier(_options.MemberHouseholdColumn)} " +
                $"FROM {dialect.EscapeIdentifier(_options.MemberTable)} " +
                $"WHERE {dialect.EscapeIdentifier(_options.MemberUserIdColumn)} = @userId";

            var userIdParam = command.CreateParameter();
            userIdParam.ParameterName = "@userId";
            userIdParam.DbType = DbType.String;
            userIdParam.Value = userId;
            command.Parameters.Add(userIdParam);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var householdId = GetString(reader, 0);
            if (string.IsNullOrWhiteSpace(householdId))
                return null;

            return new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.HouseholdClaimKey] = householdId,
            };
        }

        private static string? GetString(DbDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();

        private static IReadOnlyList<string> ParseRoles(string? rawRoles)
        {
            if (string.IsNullOrWhiteSpace(rawRoles))
                return Array.Empty<string>();

            return rawRoles
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }
    }
}
