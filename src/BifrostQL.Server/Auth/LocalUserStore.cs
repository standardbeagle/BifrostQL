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
        /// Absolute lifetime of a login session cookie, counted from sign-in. Activity does not
        /// extend it. Defaults to 8 hours.
        /// </summary>
        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

        /// <summary>
        /// How often an issued session re-reads its user row. A deleted user is signed out and a
        /// changed tenant, role list or household replaces the cookie's claims, at the first
        /// request after this interval. <see cref="TimeSpan.Zero"/> re-reads on every request.
        /// Defaults to 1 minute.
        /// </summary>
        public TimeSpan SessionRevalidationInterval { get; set; } = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Result of a credential verification attempt. <see cref="Succeeded"/> is true only
    /// when the user exists and the submitted password matches the stored hash; the
    /// <see cref="Identity"/> is populated only on success.
    /// </summary>
    public sealed record LocalLoginResult
    {
        private LocalLoginResult(bool succeeded, AppIdentity? identity)
        {
            Succeeded = succeeded;
            Identity = identity;
        }

        /// <summary>Whether the credentials were valid.</summary>
        public bool Succeeded { get; }

        /// <summary>The authenticated identity, or <c>null</c> when verification failed.</summary>
        public AppIdentity? Identity { get; }

        /// <summary>A successful result carrying the resolved identity.</summary>
        public static LocalLoginResult Success(AppIdentity identity) => new(true, identity);

        /// <summary>
        /// A failed result. Used for both "no such user" and "wrong password" so the
        /// caller cannot distinguish the two and leak account existence.
        /// </summary>
        public static LocalLoginResult Failure() => new(false, null);
    }

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
        /// Creates a store over the shared connection factory.
        /// </summary>
        /// <param name="connFactory">
        /// Server-side database connection factory. The connection string it holds never
        /// reaches the client.
        /// </param>
        /// <param name="options">Table and column configuration for the app-user rows.</param>
        /// <param name="passwordHasher">
        /// Password hasher used to verify the submitted password against the stored hash.
        /// Defaults to <see cref="PasswordHasher{TUser}"/> when not supplied.
        /// </param>
        public LocalUserStore(
            IDbConnFactory connFactory,
            LocalAuthOptions options,
            IPasswordHasher<string>? passwordHasher = null)
        {
            _connFactory = connFactory ?? throw new ArgumentNullException(nameof(connFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _passwordHasher = passwordHasher ?? new PasswordHasher<string>();
            // Compute the throwaway hash once so a user-miss can verify against it in
            // constant-ish time relative to a real verification (see _dummyHash).
            _dummyHash = _passwordHasher.HashPassword(DummyUser, DummyPassword);
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

            var verification = _passwordHasher.VerifyHashedPassword(login, row.PasswordHash, password);
            if (verification == PasswordVerificationResult.Failed)
                return LocalLoginResult.Failure();

            // A hash in an older format or below the current work factor verified, so the
            // plaintext is in hand: upgrade the stored hash now, or it stays weak forever.
            if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                await StorePasswordHashAsync(conn, dialect, row.Id, _passwordHasher.HashPassword(login, password), cancellationToken)
                    .ConfigureAwait(false);

            return LocalLoginResult.Success(
                await BuildIdentityAsync(conn, dialect, row, login, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>
        /// Re-reads the user by its stable id and rebuilds the identity a fresh login would
        /// produce (tenant, roles, household), without a password. Null when the row is gone.
        /// The cookie session revalidates against this, so a role change, tenant move or
        /// deleted row reaches an already-issued session within
        /// <see cref="LocalAuthOptions.SessionRevalidationInterval"/>.
        /// </summary>
        public async Task<AppIdentity?> FindIdentityByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var dialect = _connFactory.Dialect;
            await using var conn = _connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var row = await ReadUserRowAsync(conn, dialect, _options.IdColumn, id, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.Login))
                return null;

            return await BuildIdentityAsync(conn, dialect, row, row.Login, cancellationToken).ConfigureAwait(false);
        }

        private sealed record UserRow(
            string? Id, string? PasswordHash, string? DisplayName, string? TenantId, string? Roles, string? Login);

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
                $"{dialect.EscapeIdentifier(_options.LoginColumn)} " +
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
                GetString(reader, 3), GetString(reader, 4), GetString(reader, 5));
        }

        private async Task StorePasswordHashAsync(
            DbConnection conn, ISqlDialect dialect, string id, string hash, CancellationToken cancellationToken)
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                $"UPDATE {dialect.EscapeIdentifier(_options.UserTable)} " +
                $"SET {dialect.EscapeIdentifier(_options.PasswordHashColumn)} = @hash " +
                $"WHERE {dialect.EscapeIdentifier(_options.IdColumn)} = @id";

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

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<AppIdentity> BuildIdentityAsync(
            DbConnection conn, ISqlDialect dialect, UserRow row, string login, CancellationToken cancellationToken)
        {
            var claims = await ResolveMemberClaimsAsync(conn, dialect, row.Id!, cancellationToken)
                .ConfigureAwait(false);

            return new AppIdentity(
                id: row.Id!,
                provider: ProviderName,
                email: login,
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
