using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// RED/GREEN coverage for local DB-backed user login: credential verification against
    /// the app-user table (valid, invalid, missing user), and the AppIdentity contract
    /// produced on success flowing through to the same UserContext keys OIDC will produce.
    /// </summary>
    public sealed class LocalAuthTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly LocalAuthOptions _options = new();

        public LocalAuthTests()
        {
            // A unique temp-file SQLite database per test class run. LocalUserStore opens
            // fresh connections through the factory, so a file-backed db (not :memory:)
            // is required for the rows to be visible across connections.
            _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-localauth-{Guid.NewGuid():N}.db");
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

            // The stored value is a real ASP.NET Core PasswordHasher hash — never plaintext.
            var hasher = new PasswordHasher<string>();
            var storedHash = hasher.HashPassword("alice@club.test", "correct horse battery");

            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO app_users (id, email, password_hash, display_name, tenant_id, roles) " +
                "VALUES (@id, @email, @hash, @name, @tenant, @roles)";
            AddParam(insert, "@id", "user-1");
            AddParam(insert, "@email", "alice@club.test");
            AddParam(insert, "@hash", storedHash);
            AddParam(insert, "@name", "Alice Member");
            AddParam(insert, "@tenant", "club-7");
            AddParam(insert, "@roles", "admin,editor");
            insert.ExecuteNonQuery();
        }

        private static void AddParam(Microsoft.Data.Sqlite.SqliteCommand cmd, string name, string value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        private LocalUserStore CreateStore()
            => new(new SqliteDbConnFactory(_connectionString), _options);

        [Fact]
        public async Task VerifyCredentials_ValidPassword_SucceedsWithAppIdentity()
        {
            // Arrange
            var store = CreateStore();

            // Act
            var result = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");

            // Assert
            result.Succeeded.Should().BeTrue();
            result.Identity.Should().NotBeNull();
            result.Identity!.Id.Should().Be("user-1");
            result.Identity.Provider.Should().Be("local");
            result.Identity.Email.Should().Be("alice@club.test");
            result.Identity.DisplayName.Should().Be("Alice Member");
            result.Identity.TenantId.Should().Be("club-7");
            result.Identity.Roles.Should().BeEquivalentTo("admin", "editor");
        }

        [Fact]
        public async Task VerifyCredentials_WrongPassword_Fails()
        {
            // Arrange
            var store = CreateStore();

            // Act
            var result = await store.VerifyCredentialsAsync("alice@club.test", "wrong password");

            // Assert
            result.Succeeded.Should().BeFalse();
            result.Identity.Should().BeNull();
        }

        [Fact]
        public async Task VerifyCredentials_MissingUser_Fails()
        {
            // Arrange
            var store = CreateStore();

            // Act
            var result = await store.VerifyCredentialsAsync("nobody@club.test", "any password");

            // Assert
            result.Succeeded.Should().BeFalse();
            result.Identity.Should().BeNull();
        }

        [Theory]
        [InlineData("", "password")]
        [InlineData("alice@club.test", "")]
        public async Task VerifyCredentials_BlankCredential_FailsWithoutHittingDb(string login, string password)
        {
            // Arrange
            var store = CreateStore();

            // Act
            var result = await store.VerifyCredentialsAsync(login, password);

            // Assert
            result.Succeeded.Should().BeFalse();
            result.Identity.Should().BeNull();
        }

        [Fact]
        public async Task SuccessfulLogin_FlowsToUserContext_WithTenantRolesAndAuditKeys()
        {
            // Arrange: verify a local login, then round-trip through the principal builder
            // and BifrostContext's AppIdentity reconstruction the way the live pipeline does.
            var store = CreateStore();
            var login = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");
            login.Succeeded.Should().BeTrue();

            var principal = LocalAuthEndpoint.BuildPrincipal(login.Identity!);

            // Act: reconstruct the identity from the ClaimsPrincipal, then map to UserContext.
            var rebuilt = BifrostContext.BuildAppIdentity(principal);
            var userContext = new IdentityContextMapper().ToUserContext(rebuilt);

            // Assert: the UserContext carries the audit id, roles, and tenant the security modules read.
            userContext[MetadataKeys.Auth.DefaultUserAuditKey].Should().Be("user-1");
            userContext[MetadataKeys.Auth.DefaultRolesContextKey]
                .Should().BeEquivalentTo(new[] { "admin", "editor" });
            userContext[MetadataKeys.Auth.DefaultTenantContextKey].Should().Be("club-7");
        }

        [Fact]
        public void BuildPrincipal_ProducesAuthenticatedCookiePrincipal()
        {
            // Arrange
            var identity = new AppIdentity(
                id: "user-9",
                provider: "local",
                email: "bob@club.test",
                displayName: "Bob",
                tenantId: "club-3",
                roles: new[] { "member" });

            // Act
            var principal = LocalAuthEndpoint.BuildPrincipal(identity);

            // Assert
            principal.Identity.Should().NotBeNull();
            principal.Identity!.IsAuthenticated.Should().BeTrue();
            principal.Identity.AuthenticationType.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
            principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("user-9");
            principal.IsInRole("member").Should().BeTrue();
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
