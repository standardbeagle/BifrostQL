using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// H14: the protocol-frontend mount (<see cref="BifrostFrontendMiddleware"/>,
    /// <c>UseBifrostFrontend</c> / <c>UseBifrostGraphQL</c>) is an HTTP front door in its own
    /// <c>Map</c> branch, so the per-endpoint gate <c>UseBifrostEndpoints</c> installs inside the
    /// GraphQL branch never covered it. It projected the caller with
    /// <c>BifrostAuthContextFactory.Resolve(context).CreateUserContext(context)</c> and executed
    /// whatever came back — and for an unauthenticated caller that is an EMPTY user context, which
    /// only stops tables that DECLARE tenant metadata.
    ///
    /// <para>The fixture table therefore has NO tenant metadata
    /// (.claude/rules/protocol-adapter-security.md invariant 12): a tenant-scoped fixture cannot
    /// manifest this bug, because the empty context already refuses it, so such a test would pass
    /// against the buggy middleware and guard nothing.</para>
    /// </summary>
    public sealed class FrontendMountAuthTests
    {
        private const string MountPath = "/graphql";
        private const string ClaimsHeader = "X-Test-Claims";

        /// <summary>
        /// A host whose ONLY pipeline entry is the protocol-frontend mount, at the same path as
        /// the registered GraphQL endpoint — the shape a host takes when it drives the frontend
        /// seam directly instead of through <c>UseBifrostEndpoints</c>.
        /// </summary>
        private static async Task<IHost> BuildHostAsync(string dbPath, bool disableAuth)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // No tenant-filter metadata anywhere: an empty user context does not scope this
                // table away, which is exactly why the missing gate leaked its rows.
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY, name TEXT);" +
                    "INSERT INTO widgets (id, name) VALUES (1, 'gadget-secret');";
                cmd.ExecuteNonQuery();
            }

            var jwt = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Authority"] = "https://login.example.test",
                    ["JwtSettings:ClientId"] = "test-client",
                })
                .Build();

            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.BindJwtSettings(jwt.GetSection("JwtSettings"));
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={dbPath}";
                            e.Provider = "sqlite";
                            e.Path = MountPath;
                            e.PlaygroundPath = "/graphiql";
                            e.DisableAuth = disableAuth;
                        });
                    });
                    services.AddBifrostEngine();
                    // No OIDC mapper is registered for any issuer, so an authenticated principal
                    // that carries one must be refused rather than read through the local claim
                    // path (BifrostContext.ResolveOidcMapper).
                    services.AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()));
                });
                web.Configure(app =>
                {
                    // Stands in for the deployment's authentication middleware: lands a principal
                    // on the request and nothing more.
                    app.Use(async (context, next) =>
                    {
                        var header = context.Request.Headers[ClaimsHeader].ToString();
                        if (!string.IsNullOrEmpty(header))
                            context.User = DecodePrincipal(header);
                        await next(context);
                    });
                    app.UseBifrostGraphQL(MountPath);
                });
            });

            return await builder.StartAsync();
        }

        private static HttpRequestMessage WidgetRead(ClaimsPrincipal? principal = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, MountPath)
            {
                Content = new StringContent(
                    "{\"query\":\"{ widgets { data { id name } } }\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            if (principal is not null)
                request.Headers.Add(ClaimsHeader, EncodePrincipal(principal));
            return request;
        }

        private static string EncodePrincipal(ClaimsPrincipal principal) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray()));

        private static ClaimsPrincipal DecodePrincipal(string header)
        {
            var claims = JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
                .Select(pair => new Claim(pair[0], pair[1]));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        }

        private static async Task WithHostAsync(bool disableAuth, Func<HttpClient, Task> body)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"frontend-auth-{Guid.NewGuid():N}.db");
            using var host = await BuildHostAsync(dbPath, disableAuth);
            try
            {
                await body(host.GetTestClient());
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        /// <summary>
        /// The defect itself: an unauthenticated POST whose Content-Type matches the mounted
        /// frontend read a table with no tenant metadata. Pre-fix this returns 200 with the row.
        /// </summary>
        [Fact]
        public async Task UnauthenticatedRequest_ToAuthRequiredMount_IsRefusedWithoutData()
        {
            await WithHostAsync(disableAuth: false, async client =>
            {
                using var response = await client.SendAsync(WidgetRead());
                var body = await response.Content.ReadAsStringAsync();

                body.Should().NotContain("gadget-secret",
                    "a caller that presented no identity must not read a table that declares no tenant metadata");
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    "the mount serves an endpoint that requires authentication, so it must refuse before executing");
            });
        }

        /// <summary>
        /// Anonymous service stays reachable, but only through the endpoint's explicit
        /// <c>DisableAuth</c> opt-in — never as the ambient default.
        /// </summary>
        [Fact]
        public async Task AnonymousOptInMount_StillServesUnauthenticatedCallers()
        {
            await WithHostAsync(disableAuth: true, async client =>
            {
                using var response = await client.SendAsync(WidgetRead());
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                body.Should().Contain("gadget-secret",
                    "an endpoint explicitly configured for anonymous access must keep serving");
            });
        }

        /// <summary>
        /// An authenticated caller is served — the gate refuses for the ABSENCE of identity, not
        /// for the presence of a request.
        /// </summary>
        [Fact]
        public async Task AuthenticatedRequest_IsServed()
        {
            await WithHostAsync(disableAuth: false, async client =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") }, authenticationType: "test"));

                using var response = await client.SendAsync(WidgetRead(principal));
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                body.Should().Contain("gadget-secret");
            });
        }

        /// <summary>
        /// A token from an OIDC issuer this deployment mapped no claim mapper for must be handled
        /// here — 403, fail closed — exactly as the GraphQL sibling (BifrostHttpMiddleware:52) and
        /// the chat middleware do, never escaping to the host as an unhandled fault.
        /// </summary>
        [Fact]
        public async Task UnmappedOidcIssuer_IsRefused_NotEscaped()
        {
            await WithHostAsync(disableAuth: false, async client =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "user-1"),
                        new Claim("iss", "https://idp.example.test"),
                    },
                    authenticationType: "oidc"));

                using var response = await client.SendAsync(WidgetRead(principal));
                var body = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                    "an unmapped issuer is a fail-closed refusal, not a degraded identity and not a server fault");
                body.Should().NotContain("gadget-secret");
                body.Should().NotContain("idp.example.test",
                    "the refusal states only that authentication failed; no exception text reaches the wire");
            });
        }
    }
}
