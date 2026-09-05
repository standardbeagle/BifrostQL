using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// M13: one identity/exception funnel per HTTP mount, through
    /// <see cref="BifrostIdentityGate"/>. Pre-fix: <c>BifrostHttpMiddleware</c> read the request
    /// body outside any try (malformed JSON escaped as a <c>JsonException</c> to the host) and
    /// caught only <c>UnmappedOidcIssuerException</c>, so a subject-less authenticated principal
    /// escaped as <c>InvalidOperationException</c> from <c>BifrostContext.BuildAppIdentity</c>;
    /// <c>BifrostFrontendMiddleware</c> parsed the body with no catch at all.
    ///
    /// <para>Every fact asserts the wire contract: a well-formed GraphQL-shaped error body
    /// (<c>{"errors":[...]}</c>), a condition-mapped status (400 malformed body, 403
    /// unprojectable identity), and NO exception type name or message on the wire
    /// (.claude/rules/protocol-adapter-security.md invariants 3/9/10).</para>
    /// </summary>
    public sealed class MountIdentityFunnelTests
    {
        private const string MountPath = "/graphql";
        private const string ClaimsHeader = "X-Test-Claims";

        private static async Task<IHost> BuildGraphQlHostAsync(string dbPath)
        {
            CreateDatabase(dbPath);
            var jwt = JwtConfig();

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
                            e.DisableAuth = false;
                        });
                    });
                    services.AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()));
                });
                web.Configure(app =>
                {
                    app.Use(ClaimsMiddleware);
                    app.UseBifrostEndpoints();
                });
            });

            return await builder.StartAsync();
        }

        private static async Task<IHost> BuildFrontendHostAsync(string dbPath)
        {
            CreateDatabase(dbPath);
            var jwt = JwtConfig();

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
                            e.DisableAuth = false;
                        });
                    });
                    services.AddBifrostEngine();
                    services.AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()));
                });
                web.Configure(app =>
                {
                    app.Use(ClaimsMiddleware);
                    app.UseBifrostGraphQL(MountPath);
                });
            });

            return await builder.StartAsync();
        }

        private static void CreateDatabase(string dbPath)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY, name TEXT);" +
                "INSERT INTO widgets (id, name) VALUES (1, 'gadget-secret');";
            cmd.ExecuteNonQuery();
        }

        private static IConfiguration JwtConfig() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Authority"] = "https://login.example.test",
                ["JwtSettings:ClientId"] = "test-client",
            })
            .Build();

        private static async Task ClaimsMiddleware(HttpContext context, Func<Task> next)
        {
            var header = context.Request.Headers[ClaimsHeader].ToString();
            if (!string.IsNullOrEmpty(header))
            {
                var claims = JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
                    .Select(pair => new Claim(pair[0], pair[1]));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
            }
            await next();
        }

        private static HttpRequestMessage Post(string body, ClaimsPrincipal? principal = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, MountPath)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (principal is not null)
                request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                    principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray())));
            return request;
        }

        /// <summary>An authenticated principal carrying no NameIdentifier/sub/Name claim.</summary>
        private static ClaimsPrincipal SubjectlessPrincipal() =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Email, "nosubject@example.test") },
                authenticationType: "test"));

        /// <summary>
        /// A well-formed authenticated principal. Malformed-body facts must carry one: the
        /// identity gate runs BEFORE the body is read, so an anonymous malformed request is
        /// refused as 401 and never exercises the parse path.
        /// </summary>
        private static ClaimsPrincipal ValidPrincipal() =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                authenticationType: "test"));

        private static async Task WithHostAsync(bool frontend, Func<HttpClient, Task> body)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"m13-funnel-{Guid.NewGuid():N}.db");
            using var host = frontend ? await BuildFrontendHostAsync(dbPath) : await BuildGraphQlHostAsync(dbPath);
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

        private static async Task AssertGraphQlShapedError(HttpResponseMessage response, HttpStatusCode status)
        {
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(status,
                "the mount must map the condition to its own status, not escape a fault to the host");
            body.Should().NotContain("gadget-secret", "a refused request must not carry data");

            using var doc = JsonDocument.Parse(body);
            doc.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue(
                "the refusal must be a well-formed GraphQL-shaped error body, not a bare status or a host error page");
            errors.GetArrayLength().Should().BeGreaterThan(0);

            body.Should().NotContain("JsonException", "exception type names never reach the wire");
            body.Should().NotContain("InvalidOperationException", "exception type names never reach the wire");
            body.Should().NotContain("subject claim", "exception message text never reaches the wire");
        }

        [Fact]
        public async Task GraphQlMount_MalformedJsonBody_ReturnsWellFormed400()
        {
            await WithHostAsync(frontend: false, async client =>
            {
                using var response = await client.SendAsync(Post("{", ValidPrincipal()));
                await AssertGraphQlShapedError(response, HttpStatusCode.BadRequest);
            });
        }

        [Fact]
        public async Task GraphQlMount_SubjectlessPrincipal_Returns403WithoutExceptionText()
        {
            await WithHostAsync(frontend: false, async client =>
            {
                using var response = await client.SendAsync(
                    Post("{\"query\":\"{ widgets { data { id name } } }\"}", SubjectlessPrincipal()));
                await AssertGraphQlShapedError(response, HttpStatusCode.Forbidden);
            });
        }

        [Fact]
        public async Task FrontendMount_MalformedJsonBody_ReturnsWellFormed400()
        {
            await WithHostAsync(frontend: true, async client =>
            {
                using var response = await client.SendAsync(Post("{", ValidPrincipal()));
                await AssertGraphQlShapedError(response, HttpStatusCode.BadRequest);
            });
        }

        [Fact]
        public async Task FrontendMount_SubjectlessPrincipal_Returns403WithoutExceptionText()
        {
            await WithHostAsync(frontend: true, async client =>
            {
                using var response = await client.SendAsync(
                    Post("{\"query\":\"{ widgets { data { id name } } }\"}", SubjectlessPrincipal()));
                await AssertGraphQlShapedError(response, HttpStatusCode.Forbidden);
            });
        }

        /// <summary>
        /// The workflow sidecar helper is the one projection seam with no wire of its own, so
        /// it cannot answer 403 — but it must not answer with an EMPTY context either: an
        /// empty context is not a refusal (invariant 12), the sidecar's own IsAuthenticated
        /// gate has already admitted the principal, and the executor serves reads AND writes
        /// on an empty context. A refused identity throws a typed, constant-message exception.
        /// </summary>
        [Fact]
        public void WorkflowHelper_SubjectlessPrincipal_ThrowsRejected_NeverAnEmptyContext()
        {
            var context = new DefaultHttpContext { User = SubjectlessPrincipal() };

            var act = () => context.GetBifrostUserContext();

            var thrown = act.Should().Throw<BifrostIdentityRejectedException>(
                "an authenticated caller Bifrost cannot identify must be refused, not served as anonymous").Which;
            thrown.Message.Should().Be(BifrostIdentityRejectedException.WireMessage);
            thrown.Message.Should().NotContain("subject claim", "exception detail never reaches the wire");
        }

        [Fact]
        public void WorkflowHelper_AnonymousRequest_YieldsEmptyContext()
        {
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

            context.GetBifrostUserContext().Should().BeEmpty(
                "an unauthenticated request is the sidecar's own gate to refuse; the helper stays a projection");
        }

        /// <summary>
        /// Source-scan guard (M13 flow item 3): the HTTP mounts must never project a caller
        /// directly — <c>.CreateUserContext(</c> may appear only in
        /// <c>BifrostIdentityGate.cs</c> and in the non-HTTP protocol-adapter authenticators,
        /// which project their own carrier types rather than an <c>HttpContext</c> mount. The
        /// positive anchor on the gate is asserted too, so a drifted regex cannot read as GREEN.
        /// </summary>
        [Fact]
        public void ServerSources_NoHttpMountProjectsOutsideTheIdentityGate()
        {
            var serverRoot = LocateServerSourceRoot();
            serverRoot.Should().NotBeNull(
                "the BifrostQL.Server source directory must be locatable from the test assembly");

            var files = Directory.GetFiles(serverRoot!, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();
            files.Should().NotBeEmpty("the scan must actually match source files to guard anything");

            var projectionCall = new Regex(@"\.CreateUserContext\(", RegexOptions.Compiled);
            var perFile = files.ToDictionary(f => f, File.ReadAllText);

            // Non-HTTP protocol adapters project their own carrier (LDAP bind, RESP AUTH, S3
            // SigV4, ...), not an HttpContext mount, and keep their own per-adapter funnels.
            var adapterHomes = new HashSet<string>(StringComparer.Ordinal)
            {
                "LdapBindAuthenticator.cs", "FeedAuthenticator.cs", "RespConnectionHandler.cs",
                "PrometheusScrapeScopeResolver.cs", "ODataAuthenticator.cs", "GrpcIdentityGate.cs",
                "S3SigV4Verifier.cs", "PgConnectionHandler.cs",
            };

            var gateFile = files.SingleOrDefault(f => Path.GetFileName(f) == "BifrostIdentityGate.cs");
            gateFile.Should().NotBeNull("the identity gate is the one projection seam this scan anchors on");
            projectionCall.IsMatch(perFile[gateFile!]).Should().BeTrue(
                "the scan's anchor must match the gate itself, or it matches nothing and guards nothing");

            var offenders = files
                .Where(f => f != gateFile
                         && !adapterHomes.Contains(Path.GetFileName(f))
                         && projectionCall.IsMatch(perFile[f]))
                .ToList();
            offenders.Should().BeEmpty(
                "every HTTP mount's identity projection lives in BifrostIdentityGate.Project; " +
                "a second copy is a second identity decision that will drift. Offenders: "
                + string.Join(", ", offenders.Select(Path.GetFileName)));
        }

        private static string? LocateServerSourceRoot([CallerFilePath] string callerFilePath = "")
        {
            if (string.IsNullOrEmpty(callerFilePath))
                return null;
            var testProjectDir = Path.GetDirectoryName(callerFilePath);
            if (testProjectDir == null)
                return null;
            var repoRoot = Path.GetFullPath(Path.Combine(testProjectDir, "..", ".."));
            var serverDir = Path.Combine(repoRoot, "src", "BifrostQL.Server");
            return Directory.Exists(serverDir) ? serverDir : null;
        }
    }
}
