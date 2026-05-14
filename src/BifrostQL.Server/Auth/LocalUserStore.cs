using System.Data;
using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
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

        /// <summary>Login path for the local auth endpoint. Defaults to <c>/auth/login</c>.</summary>
        public string LoginPath { get; set; } = "/auth/login";

        /// <summary>Logout path for the local auth endpoint. Defaults to <c>/auth/logout</c>.</summary>
        public string LogoutPath { get; set; } = "/auth/logout";

        /// <summary>Read-session path for the local auth endpoint. Defaults to <c>/auth/session</c>.</summary>
        public string SessionPath { get; set; } = "/auth/session";
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
        }

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

            await using var command = conn.CreateCommand();
            command.CommandText =
                $"SELECT {dialect.EscapeIdentifier(_options.IdColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.PasswordHashColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.DisplayNameColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.TenantColumn)}, " +
                $"{dialect.EscapeIdentifier(_options.RolesColumn)} " +
                $"FROM {dialect.EscapeIdentifier(_options.UserTable)} " +
                $"WHERE {dialect.EscapeIdentifier(_options.LoginColumn)} = @login";

            var loginParam = command.CreateParameter();
            loginParam.ParameterName = "@login";
            loginParam.DbType = DbType.String;
            loginParam.Value = login;
            command.Parameters.Add(loginParam);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return LocalLoginResult.Failure();

            var id = GetString(reader, 0);
            var storedHash = GetString(reader, 1);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(storedHash))
                return LocalLoginResult.Failure();

            var verification = _passwordHasher.VerifyHashedPassword(login, storedHash, password);
            if (verification == PasswordVerificationResult.Failed)
                return LocalLoginResult.Failure();

            var displayName = GetString(reader, 2);
            var tenantId = GetString(reader, 3);
            var roles = ParseRoles(GetString(reader, 4));

            var identity = new AppIdentity(
                id: id,
                provider: "local",
                email: login,
                displayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                tenantId: string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                roles: roles);

            return LocalLoginResult.Success(identity);
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
