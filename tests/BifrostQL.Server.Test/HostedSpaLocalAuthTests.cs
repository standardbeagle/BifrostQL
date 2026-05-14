using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BifrostQL.Samples.HostedSpa;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// WebApplicationFactory coverage for the HostedSpa sample's local-auth wiring: the
    /// host calls AddBifrostLocalAuth against the Membership Manager app_users table, and
    /// SampleDatabase seeds a deterministic first-admin whose password is hashed with the
    /// real ASP.NET Core PasswordHasher. These tests verify a self-hosted club can sign in
    /// with local auth out of the box — POST /auth/login succeeds with the seeded
    /// credentials and /auth/session returns the admin identity with roles and tenant.
    /// </summary>
    public sealed class HostedSpaLocalAuthTests : IClassFixture<HostedSpaLocalAuthTests.LocalAuthFactory>
    {
        private readonly LocalAuthFactory _factory;

        public HostedSpaLocalAuthTests(LocalAuthFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Login_WithSeededFirstAdmin_SucceedsAndSessionReturnsAdminIdentity()
        {
            // Arrange: a cookie-tracking client so the login cookie carries to /auth/session.
            var client = _factory.CreateClient();

            // Act: sign in with the deterministic first-admin credentials seeded by
            // SampleDatabase. The stored hash was produced by the real PasswordHasher, so
            // LocalUserStore.VerifyCredentialsAsync verifies it without any test double.
            var login = await client.PostAsJsonAsync(
                "/auth/login",
                new { login = SampleDatabase.FirstAdminEmail, password = SampleDatabase.FirstAdminPassword });

            // Assert: the login endpoint issues the session cookie.
            login.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Act: read the session back through the issued cookie.
            var session = await client.GetAsync("/auth/session");

            // Assert: the admin identity comes back with roles and tenant populated.
            session.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("email").GetString().Should().Be(SampleDatabase.FirstAdminEmail);
            root.GetProperty("provider").GetString().Should().Be("local");
            root.GetProperty("tenantId").GetString().Should().Be(SampleDatabase.FirstAdminTenantId);
            root.GetProperty("roles").EnumerateArray().Select(e => e.GetString())
                .Should().Contain(SampleDatabase.FirstAdminRole);
        }

        [Fact]
        public async Task Login_WithWrongPassword_IsRejected()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act: the seeded admin email with a password that does not match the stored hash.
            var login = await client.PostAsJsonAsync(
                "/auth/login",
                new { login = SampleDatabase.FirstAdminEmail, password = "not the seeded password" });

            // Assert: rejected, and no session is established.
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var session = await client.GetAsync("/auth/session");
            session.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// Hosts the HostedSpa sample pointed at a fresh, uniquely named SQLite database so
        /// the seed always runs and runs do not collide with a stale file.
        /// </summary>
        public sealed class LocalAuthFactory : WebApplicationFactory<Program>
        {
            private readonly string _dbPath =
                Path.Combine(Path.GetTempPath(), $"hostedspa-localauth-{Guid.NewGuid():N}.db");

            protected override IHost CreateHost(IHostBuilder builder)
            {
                builder.ConfigureHostConfiguration(config =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:bifrost"] = $"Data Source={_dbPath}",
                    }));

                return base.CreateHost(builder);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
        }
    }
}
