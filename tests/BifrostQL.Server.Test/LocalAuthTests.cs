using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using NSubstitute;
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
                    "display_name TEXT, tenant_id TEXT, roles TEXT);" +
                    // A members slice so the optional household-claim enrichment has a
                    // member row to resolve from the authenticated user_id. alice (user-1)
                    // is linked to a member with a household; bob (user-2) is linked to a
                    // member with no household.
                    "CREATE TABLE members (" +
                    "member_id INTEGER PRIMARY KEY, user_id TEXT, household_id TEXT)";
                create.ExecuteNonQuery();
            }

            // The stored value is a real ASP.NET Core PasswordHasher hash — never plaintext.
            var hasher = new PasswordHasher<string>();
            var storedHash = hasher.HashPassword("alice@club.test", "correct horse battery");
            var bobHash = hasher.HashPassword("bob@club.test", "another good password");

            using (var insert = conn.CreateCommand())
            {
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

            using (var insertBob = conn.CreateCommand())
            {
                insertBob.CommandText =
                    "INSERT INTO app_users (id, email, password_hash, display_name, tenant_id, roles) " +
                    "VALUES (@id, @email, @hash, @name, @tenant, @roles)";
                AddParam(insertBob, "@id", "user-2");
                AddParam(insertBob, "@email", "bob@club.test");
                AddParam(insertBob, "@hash", bobHash);
                AddParam(insertBob, "@name", "Bob Member");
                AddParam(insertBob, "@tenant", "club-7");
                AddParam(insertBob, "@roles", "member");
                insertBob.ExecuteNonQuery();
            }

            using var members = conn.CreateCommand();
            members.CommandText =
                "INSERT INTO members (member_id, user_id, household_id) VALUES (1, 'user-1', 'house-42');" +
                "INSERT INTO members (member_id, user_id, household_id) VALUES (2, 'user-2', NULL)";
            members.ExecuteNonQuery();
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

        [Fact]
        public async Task VerifyCredentials_MissingUser_StillInvokesPasswordHasher_NoTimingOracle()
        {
            // A missing user must still spend a password-hash verification so the response
            // time does not distinguish "no such user" from "wrong password". The dummy-hash
            // verification is the constant-time guard: assert VerifyHashedPassword IS called
            // on the miss path (it previously returned before any hashing work).
            var hasher = Substitute.For<IPasswordHasher<string>>();
            hasher.VerifyHashedPassword(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(PasswordVerificationResult.Failed);
            var store = new LocalUserStore(new SqliteDbConnFactory(_connectionString), _options, hasher);

            var result = await store.VerifyCredentialsAsync("nobody@club.test", "any password");

            result.Succeeded.Should().BeFalse();
            hasher.Received().VerifyHashedPassword(
                "nobody@club.test", Arg.Any<string>(), "any password");
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

        [Fact]
        public void BuildAppIdentity_AuthenticatedPrincipalWithoutSubject_Throws()
        {
            // A misconfigured token (authenticated but carrying no subject claim)
            // must fail rather than silently collapse to the "anonymous" sentinel,
            // which would run tenant/row-scope checks against a bogus identity.
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, "x@y.test") }, "TestAuth"));

            var act = () => BifrostContext.BuildAppIdentity(principal);

            act.Should().Throw<InvalidOperationException>().WithMessage("*no subject*");
        }

        [Fact]
        public void BuildAppIdentity_UnauthenticatedPrincipal_IsAnonymous()
        {
            // An unauthenticated principal legitimately has no subject → anonymous.
            var principal = new ClaimsPrincipal(new ClaimsIdentity());

            var identity = BifrostContext.BuildAppIdentity(principal);

            identity.Id.Should().Be("anonymous");
        }

        [Fact]
        public async Task SessionEndpoint_Unauthenticated_Returns401()
        {
            // Arrange: a context whose User is an anonymous (unauthenticated) principal.
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());

            // Act
            await LocalAuthEndpoint.HandleSessionAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        }

        [Fact]
        public async Task SessionEndpoint_NonGetMethod_Returns405()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());

            // Act
            await LocalAuthEndpoint.HandleSessionAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        }

        [Fact]
        public async Task SessionEndpoint_Authenticated_Returns200WithCamelCaseAppIdentity()
        {
            // Arrange: an authenticated cookie principal built the same way login issues it.
            var store = CreateStore();
            var login = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");
            login.Succeeded.Should().BeTrue();
            var principal = LocalAuthEndpoint.BuildPrincipal(login.Identity!);

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.User = principal;
            var body = new MemoryStream();
            context.Response.Body = body;

            // Act
            await LocalAuthEndpoint.HandleSessionAsync(context);

            // Assert: 200 + the camelCase AppIdentity contract the app-shell SessionProvider reads.
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.ContentType.Should().StartWith("application/json");

            body.Position = 0;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            root.GetProperty("id").GetString().Should().Be("user-1");
            root.GetProperty("email").GetString().Should().Be("alice@club.test");
            root.GetProperty("displayName").GetString().Should().Be("Alice Member");
            root.GetProperty("provider").GetString().Should().Be("local");
            root.GetProperty("tenantId").GetString().Should().Be("club-7");
            root.GetProperty("roles").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo("admin", "editor");
            root.GetProperty("orgIds").GetArrayLength().Should().Be(0);
            root.GetProperty("permissions").GetArrayLength().Should().Be(0);
            root.TryGetProperty("claims", out _).Should().BeTrue();
        }

        // ---- household_id claim enrichment ----
        //
        // The MM households policy row-scope is `household_id = {household_id}`, so the
        // authenticated caller's user context must carry a household_id claim resolved
        // from their member row. Enrichment is opt-in: a deployment points LocalAuthOptions
        // at the members table + the user-id/household-id columns, and a successful login
        // resolves the member row and surfaces household_id as an AppIdentity claim that
        // round-trips through the cookie into the UserContext.

        private LocalUserStore CreateStoreWithHouseholdEnrichment()
            => new(new SqliteDbConnFactory(_connectionString), new LocalAuthOptions
            {
                MemberTable = "members",
                MemberUserIdColumn = "user_id",
                MemberHouseholdColumn = "household_id",
            });

        [Fact]
        public async Task VerifyCredentials_WithHouseholdEnrichment_SurfacesHouseholdClaim()
        {
            // Arrange: alice (user-1) is linked to a member with household 'house-42'.
            var store = CreateStoreWithHouseholdEnrichment();

            // Act
            var result = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");

            // Assert: the resolved household_id is surfaced as a provider claim.
            result.Succeeded.Should().BeTrue();
            result.Identity!.Claims.Should().ContainKey("household_id");
            result.Identity.Claims["household_id"].Should().Be("house-42");
        }

        [Fact]
        public async Task VerifyCredentials_MemberWithoutHousehold_OmitsHouseholdClaim()
        {
            // Arrange: bob (user-2) is linked to a member row whose household_id is NULL.
            var store = CreateStoreWithHouseholdEnrichment();

            // Act
            var result = await store.VerifyCredentialsAsync("bob@club.test", "another good password");

            // Assert: no household claim when the member has no household.
            result.Succeeded.Should().BeTrue();
            result.Identity!.Claims.Should().NotContainKey("household_id");
        }

        [Fact]
        public async Task VerifyCredentials_WithoutHouseholdEnrichmentConfigured_OmitsHouseholdClaim()
        {
            // Arrange: the default options do not configure member enrichment.
            var store = CreateStore();

            // Act
            var result = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");

            // Assert: enrichment is opt-in — no extra query, no household claim.
            result.Succeeded.Should().BeTrue();
            result.Identity!.Claims.Should().NotContainKey("household_id");
        }

        [Fact]
        public async Task HouseholdClaim_RoundTripsThroughCookie_IntoUserContext()
        {
            // Arrange: a full login that resolves alice's household, then the same
            // principal -> AppIdentity -> UserContext round-trip the live pipeline runs.
            var store = CreateStoreWithHouseholdEnrichment();
            var login = await store.VerifyCredentialsAsync("alice@club.test", "correct horse battery");
            login.Succeeded.Should().BeTrue();

            var principal = LocalAuthEndpoint.BuildPrincipal(login.Identity!);

            // Act
            var rebuilt = BifrostContext.BuildAppIdentity(principal);
            var userContext = new IdentityContextMapper().ToUserContext(rebuilt);

            // Assert: the caller's context carries both user_id and household_id, so the
            // MM members and households row-scopes both resolve.
            userContext[MetadataKeys.Auth.DefaultUserIdContextKey].Should().Be("user-1");
            userContext["household_id"].Should().Be("house-42");
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
