using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// MM auth 3/4 evidence: the OIDC claim mappers produce an <see cref="AppIdentity"/>
    /// with the same role/tenant claim shape <see cref="LocalUserStore"/> produces, so adding
    /// Google/Microsoft 365 OIDC login is purely a configuration change — app authorization
    /// semantics are unchanged because the security modules read the identical UserContext
    /// keys regardless of which provider authenticated the request. This is the mapper-level
    /// proof: no live OIDC provider is contacted; the OIDC side is a synthetic principal.
    /// </summary>
    public sealed class OidcLocalAuthParityTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public OidcLocalAuthParityTests()
        {
            // A file-backed SQLite db (not :memory:) so LocalUserStore's fresh connections
            // see the seeded app-user row — the same pattern LocalAuthTests uses.
            _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-oidc-parity-{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_dbPath}";
            SeedAppUsersTable();
        }

        private void SeedAppUsersTable()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE app_users (" +
                    "id TEXT PRIMARY KEY, email TEXT, password_hash TEXT, " +
                    "display_name TEXT, tenant_id TEXT, roles TEXT)";
                create.ExecuteNonQuery();
            }

            var hasher = new PasswordHasher<string>();
            var storedHash = hasher.HashPassword("alice@club.test", "correct horse battery");

            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO app_users (id, email, password_hash, display_name, tenant_id, roles) " +
                "VALUES ('user-1', 'alice@club.test', @hash, 'Alice Member', 'club-7', 'admin,editor')";
            var p = insert.CreateParameter();
            p.ParameterName = "@hash";
            p.Value = storedHash;
            insert.Parameters.Add(p);
            insert.ExecuteNonQuery();
        }

        /// <summary>
        /// A Microsoft 365 OIDC principal carrying the same logical user as the seeded
        /// app-user row: the M365 tenant claim (<c>tid</c>) and the role claims match the
        /// app-user row's tenant_id and delimited roles column.
        /// </summary>
        private static ClaimsPrincipal Microsoft365PrincipalForAlice() => new(new ClaimsIdentity(new[]
        {
            new Claim("iss", "https://login.microsoftonline.com/club-7/v2.0"),
            new Claim("sub", "user-1"),
            new Claim("email", "alice@club.test"),
            new Claim("name", "Alice Member"),
            new Claim("tid", "club-7"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "editor"),
        }, "oauth2"));

        [Fact]
        public async Task OidcMapper_AndLocalUserStore_ProduceSameRoleAndTenantClaimShape()
        {
            // Arrange: the same logical user authenticated two ways — local DB-backed login
            // and a Microsoft 365 OIDC principal.
            var localStore = new LocalUserStore(new SqliteDbConnFactory(_connectionString), new LocalAuthOptions());
            var localLogin = await localStore.VerifyCredentialsAsync("alice@club.test", "correct horse battery");
            localLogin.Succeeded.Should().BeTrue();

            var oidcIdentity = new Microsoft365ClaimMapper().Map(Microsoft365PrincipalForAlice());

            // Act: project each AppIdentity into the UserContext dictionary the security
            // modules actually read — exactly what the live pipeline does for either path.
            var localContext = new IdentityContextMapper().ToUserContext(localLogin.Identity!);
            var oidcContext = new IdentityContextMapper().ToUserContext(oidcIdentity);

            // Assert: the role and tenant claim keys carry the identical values, so the
            // tenant filter and role-bypass checks behave the same regardless of provider.
            oidcContext[MetadataKeys.Auth.DefaultTenantContextKey]
                .Should().Be(localContext[MetadataKeys.Auth.DefaultTenantContextKey]);
            oidcContext[MetadataKeys.Auth.DefaultRolesContextKey]
                .Should().BeEquivalentTo(localContext[MetadataKeys.Auth.DefaultRolesContextKey]);
        }

        [Fact]
        public async Task OidcIdentity_ReIssuedAsLocalCookie_RebuildsToSameRoleAndTenantAsLocalLogin()
        {
            // Arrange: a local login and an OIDC login for the same logical user. The OIDC
            // identity is re-issued in the shared local-auth cookie shape (what UseUiAuth
            // does after selecting a mapper by issuer), then reconstructed the way the live
            // request pipeline reconstructs it from the cookie.
            var localStore = new LocalUserStore(new SqliteDbConnFactory(_connectionString), new LocalAuthOptions());
            var localLogin = await localStore.VerifyCredentialsAsync("alice@club.test", "correct horse battery");
            localLogin.Succeeded.Should().BeTrue();
            var localRebuilt = BifrostContext.BuildAppIdentity(
                LocalAuthEndpoint.BuildPrincipal(localLogin.Identity!));

            var oidcIdentity = new Microsoft365ClaimMapper().Map(Microsoft365PrincipalForAlice());
            var oidcRebuilt = BifrostContext.BuildAppIdentity(
                LocalAuthEndpoint.BuildPrincipal(oidcIdentity));

            // Act
            var localContext = new IdentityContextMapper().ToUserContext(localRebuilt);
            var oidcContext = new IdentityContextMapper().ToUserContext(oidcRebuilt);

            // Assert: after the cookie round-trip the OIDC login lands on the identical
            // role/tenant/audit UserContext keys local auth lands on — authorization
            // semantics are provider-agnostic end to end.
            oidcContext[MetadataKeys.Auth.DefaultTenantContextKey]
                .Should().Be(localContext[MetadataKeys.Auth.DefaultTenantContextKey]);
            oidcContext[MetadataKeys.Auth.DefaultRolesContextKey]
                .Should().BeEquivalentTo(localContext[MetadataKeys.Auth.DefaultRolesContextKey]);
            oidcContext[MetadataKeys.Auth.DefaultUserAuditKey]
                .Should().Be(localContext[MetadataKeys.Auth.DefaultUserAuditKey]);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                try { File.Delete(_dbPath); }
                catch (IOException) { /* best-effort temp cleanup */ }
            }
        }
    }
}
