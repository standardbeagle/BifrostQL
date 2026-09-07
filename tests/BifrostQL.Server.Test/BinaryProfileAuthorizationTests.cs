using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
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
    /// SEC-MED-2 regression: the binary WebSocket transport must enforce the same profile
    /// role gating and per-profile module filtering as the HTTP path. That enforcement
    /// lives in the shared <see cref="BifrostEngine"/>, which <see cref="BifrostBinaryMiddleware"/>
    /// invokes for every binary Query/Mutation (via <c>IBifrostEngine.ExecuteAsync</c>).
    /// Before the fix the engine read the base schema and singleton transformer service
    /// directly and never consulted the profile, so a binary caller could bypass
    /// <c>RequireRole</c> and — through the fail-closed transformer filter — tenant
    /// isolation / soft-delete. These tests drive the engine exactly as the middleware
    /// does, resolving the request's HttpContext through <see cref="IHttpContextAccessor"/>
    /// (populated by ASP.NET Core per request, including WebSocket upgrades).
    /// </summary>
    public sealed class BinaryProfileAuthorizationTests : IAsyncLifetime
    {
        private const string EndpointPath = "/bifrost-ws";
        private const string AdminProfile = "admin";

        private string _connectionString = null!;
        private SqliteConnection _keepAlive = null!;
        private SqliteDbConnFactory _connFactory = null!;
        private ProfileModelCache _profileCache = null!;
        private BifrostProfileRegistry _profileRegistry = null!;

        public async Task InitializeAsync()
        {
            _connectionString = $"Data Source=bifrost_binary_authz_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(_connectionString);
            await _keepAlive.OpenAsync();

            _connFactory = new SqliteDbConnFactory(_connectionString);

            await using (var conn = new SqliteConnection(_connectionString))
            {
                await conn.OpenAsync();
                var ddl = new SqliteCommand(
                    @"CREATE TABLE companies (
                        company_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL
                    );", conn);
                await ddl.ExecuteNonQueryAsync();
            }

            _profileRegistry = new BifrostProfileRegistry();
            // A role-gated profile: only callers in the "Admin" role may select it.
            _profileRegistry.Add(new BifrostProfile { Name = AdminProfile, RequireRole = "Admin" });

            var loader = new DbModelLoader(_connFactory, new MetadataLoader(Array.Empty<string>()));
            var read = await loader.ReadAsync();
            _profileCache = new ProfileModelCache(
                loader, read, Array.Empty<string>(), additionalMetadata: null, registry: _profileRegistry);
        }

        public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

        private ServiceProvider BuildProvider(string loaderPath = EndpointPath, string? secondLoaderPath = null)
        {
            var filterTransformers = new FilterTransformersWrap
            {
                Transformers = Array.Empty<IFilterTransformer>(),
            };

            var pathCache = new PathCache<Inputs>();
            var (model, schema) = _profileCache.GetFor(null);
            Inputs BuildInputs() => new(new Dictionary<string, object?>
            {
                { "connFactory", _connFactory },
                { "model", model },
                { "dbSchema", schema },
                { "profileModelCache", _profileCache },
            });
            pathCache.AddLoader(loaderPath, () => Task.FromResult(BuildInputs()));
            if (secondLoaderPath != null)
                pathCache.AddLoader(secondLoaderPath, () => Task.FromResult(BuildInputs()));

            var services = new ServiceCollection();
            services.AddSingleton<IFilterTransformers>(filterTransformers);
            services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
            {
                Transformers = Array.Empty<IMutationTransformer>(),
            });
            services.AddSingleton<IQueryTransformerService>(new QueryTransformerService(filterTransformers));
            services.AddSingleton<IQueryObservers>(new QueryObserversWrap());
            services.AddSingleton(pathCache);
            services.AddSingleton(_profileRegistry);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IDocumentExecuter>(new DocumentExecuter());
            services.AddBifrostEngine();
            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Executes "{ __typename }" through the real engine the way the binary middleware
        /// would: the request's HttpContext (carrying the profile header and any
        /// authenticated principal) is exposed via IHttpContextAccessor, and the request
        /// flows in with the same RequestServices the middleware passes.
        /// </summary>
        private async Task<BifrostResult> RunEngineAsync(string? profile, params string[] roles)
        {
            await using var provider = BuildProvider();
            var engine = provider.GetRequiredService<IBifrostEngine>();

            var context = new DefaultHttpContext { RequestServices = provider };
            if (profile != null)
                context.Request.Headers["X-BifrostQL-Profile"] = profile;
            if (roles.Length > 0)
            {
                var identity = new ClaimsIdentity(authenticationType: "test");
                foreach (var role in roles)
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                context.User = new ClaimsPrincipal(identity);
            }
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            return await engine.ExecuteAsync(new BifrostRequest
            {
                Query = "{ __typename }",
                UserContext = new Dictionary<string, object?>(),
                RequestServices = provider,
                CancellationToken = default,
            }, EndpointPath);
        }

        private static IEnumerable<string> Messages(BifrostResult result)
            => result.Errors.Select(e => e.Message);

        [Fact]
        public async Task RoleGatedProfile_Unauthenticated_IsRejected()
        {
            var result = await RunEngineAsync(AdminProfile);

            Messages(result).Should().ContainSingle()
                .Which.Should().Be("Profile requires authentication.",
                    "the binary engine must enforce the profile's RequireRole like the HTTP path, with a constant message");
        }

        [Fact]
        public async Task RoleGatedProfile_WrongRole_IsRejected()
        {
            var result = await RunEngineAsync(AdminProfile, "Viewer");

            Messages(result).Should().ContainSingle()
                .Which.Should().Be("Profile requires an additional role.");
        }

        [Fact]
        public async Task RoleGatedProfile_WithRequiredRole_PassesTheGate()
        {
            var result = await RunEngineAsync(AdminProfile, "Admin");

            Messages(result).Should().NotContain(m => m.Contains("requires"),
                "a caller holding the required role passes the profile gate");
        }

        [Fact]
        public async Task UnknownProfile_IsRejected()
        {
            var result = await RunEngineAsync("does-not-exist");

            Messages(result).Should().ContainSingle()
                .Which.Should().Contain("Unknown profile");
        }

        [Fact]
        public async Task NoProfile_DefaultProfile_ExecutesWithoutAuthorizationError()
        {
            var result = await RunEngineAsync(profile: null);

            Messages(result).Should().NotContain(m => m.Contains("requires") || m.Contains("Unknown profile"),
                "the default profile requires no role");
        }

        [Fact]
        public async Task BinaryMountPath_NotAGraphQlEndpoint_ResolvesSchemaFromRegisteredGraphQlEndpoint()
        {
            // Finding 5: the binary transport is mounted at its own path (e.g. /bifrost-ws)
            // which is NOT a registered GraphQL endpoint in the PathCache. Keying the schema
            // lookup by that path would throw ArgumentOutOfRangeException, surfacing as an
            // opaque "Query execution failed". The engine must fall back to the single
            // registered GraphQL endpoint and execute normally.
            await using var provider = BuildProvider(loaderPath: "/graphql");
            var engine = provider.GetRequiredService<IBifrostEngine>();

            var context = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            var result = await engine.ExecuteAsync(new BifrostRequest
            {
                Query = "{ __typename }",
                UserContext = new Dictionary<string, object?>(),
                RequestServices = provider,
                CancellationToken = default,
            }, "/bifrost-ws"); // binary mount path, not a registered GraphQL path

            Messages(result).Should().NotContain(m => m.Contains("Query execution failed"),
                "the binary mount path must resolve the registered GraphQL schema, not throw");
            result.Data.Should().NotBeNull("the query executes against the resolved schema");
        }

        // ---- H8(a): the binary mount's own fail-closed identity gate ----
        //
        // UseBifrostEndpoints puts its auth gate INSIDE each GraphQL Map branch, so a
        // binary transport mounted at its own path (/bifrost-ws) was never covered by it:
        // an anonymous WebSocket peer reached ExecuteRequestAsync, whose
        // CreateUserContext returns an EMPTY user context for an unauthenticated caller —
        // and the query ran under it. These facts drive the REAL pipeline (a real
        // WebSocket upgrade against a TestServer) because the defect is precisely in how
        // the mount is wired, not in anything a unit-level double would reproduce.

        private const string SecuredGraphQlPath = "/graphql/secured";
        private const string OpenGraphQlPath = "/graphql/open";
        private const string SecuredSocketPath = "/bifrost-ws-secured";
        private const string OpenSocketPath = "/bifrost-ws-open";

        private static async Task<IHost> BuildWebSocketHostAsync(
            string dbPath, bool mountBinaryBeforeAuthentication = false)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                var ddl = conn.CreateCommand();
                ddl.CommandText = "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY, name TEXT)";
                await ddl.ExecuteNonQueryAsync();
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
                            e.Path = SecuredGraphQlPath;
                            e.PlaygroundPath = "/graphiql/secured";
                            e.DisableAuth = false; // requires auth
                        });
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = $"Data Source={dbPath}";
                            e.Provider = "sqlite";
                            e.Path = OpenGraphQlPath;
                            e.PlaygroundPath = "/graphiql/open";
                            e.DisableAuth = true; // anonymous allowed
                        });
                    });
                    services.AddBifrostEngine();
                });
                web.Configure(app =>
                {
                    // Stands in for the deployment's authentication middleware: lands a
                    // principal on the upgrade request when the test asks for one.
                    void UseAuthenticationStandIn() => app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } name)
                        {
                            var identity = new ClaimsIdentity(authenticationType: "test");
                            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, name));
                            identity.AddClaim(new Claim(ClaimTypes.Name, name));
                            context.User = new ClaimsPrincipal(identity);
                        }
                        await next(context);
                    });

                    void UseBinaryMounts()
                    {
                        app.UseBifrostBinary(SecuredSocketPath, graphqlPath: SecuredGraphQlPath);
                        app.UseBifrostBinary(OpenSocketPath, graphqlPath: OpenGraphQlPath);
                    }

                    app.UseWebSockets();
                    if (mountBinaryBeforeAuthentication)
                    {
                        // The MISORDERED pipeline the H8 doc example showed: the binary mount
                        // sits ahead of the middleware that populates the principal.
                        UseBinaryMounts();
                        UseAuthenticationStandIn();
                        app.UseBifrostEndpoints();
                    }
                    else
                    {
                        UseAuthenticationStandIn();
                        app.UseBifrostEndpoints();
                        UseBinaryMounts();
                    }
                });
            });

            return await builder.StartAsync();
        }

        /// <summary>
        /// Opens the socket, sends one Query frame, and reports what came back first: the
        /// close status (when the server closed the connection) and the first non-close
        /// frame (when it answered). "No query executed" is proven by the absence of a
        /// Result/Error frame — nothing but the close arrives.
        /// </summary>
        private static async Task<(WebSocketCloseStatus? closeStatus, BifrostMessage? firstFrame)> QueryOverSocketAsync(
            IHost host, string socketPath, string? user = null)
        {
            var wsClient = host.GetTestServer().CreateWebSocketClient();
            if (user != null)
                wsClient.ConfigureRequest = req => req.Headers["X-Test-User"] = user;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            WebSocket socket;
            try
            {
                socket = await wsClient.ConnectAsync(
                    new Uri($"ws://localhost{socketPath}"), cts.Token);
            }
            catch (Exception)
            {
                // A refusal before the upgrade also means no query executed, but it carries
                // no close status; report it as such.
                return (null, null);
            }

            using (socket)
            {
                var request = new BifrostMessage
                {
                    RequestId = 7,
                    Type = BifrostMessageType.Query,
                    Query = "{ __typename }",
                };
                try
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(request.ToBytes()),
                        WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
                }
                catch (WebSocketException)
                {
                    // Server already closed the socket; fall through to the receive below.
                }

                var buffer = new byte[64 * 1024];
                try
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return (result.CloseStatus, null);

                    var frame = BifrostMessage.FromBytes(buffer.AsSpan(0, result.Count).ToArray());
                    return (null, frame);
                }
                catch (WebSocketException)
                {
                    return (socket.CloseStatus, null);
                }
            }
        }

        [Fact]
        public async Task AnonymousWebSocket_OnAuthRequiredEndpoint_IsClosed_WithoutExecutingTheQuery()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"binary-authgate-{Guid.NewGuid():N}.db");
            using var host = await BuildWebSocketHostAsync(dbPath);
            try
            {
                var (closeStatus, frame) = await QueryOverSocketAsync(host, SecuredSocketPath);

                frame.Should().BeNull(
                    "an anonymous caller on an auth-required endpoint must get NO answer frame — " +
                    "the query must never reach the engine");
                closeStatus.Should().Be(WebSocketCloseStatus.PolicyViolation,
                    "the binary mount must fail closed on the same auth requirement its GraphQL endpoint carries");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public async Task AnonymousWebSocket_OnOpenEndpoint_StillExecutes()
        {
            // Non-vacuity: the gate follows the endpoint's own DisableAuth setting rather
            // than denying every anonymous socket. Without this fact a blanket refusal
            // would pass the test above.
            var dbPath = Path.Combine(Path.GetTempPath(), $"binary-authgate-{Guid.NewGuid():N}.db");
            using var host = await BuildWebSocketHostAsync(dbPath);
            try
            {
                var (closeStatus, frame) = await QueryOverSocketAsync(host, OpenSocketPath);

                closeStatus.Should().NotBe(WebSocketCloseStatus.PolicyViolation,
                    "an endpoint that disables auth serves anonymous binary callers");
                frame.Should().NotBeNull("the anonymous query executes on an open endpoint");
                frame!.Type.Should().Be(BifrostMessageType.Result);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public async Task AuthenticatedWebSocket_OnAuthRequiredEndpoint_PassesTheGate()
        {
            // The other half of non-vacuity: a caller carrying a real principal is served.
            var dbPath = Path.Combine(Path.GetTempPath(), $"binary-authgate-{Guid.NewGuid():N}.db");
            using var host = await BuildWebSocketHostAsync(dbPath);
            try
            {
                var (closeStatus, frame) = await QueryOverSocketAsync(host, SecuredSocketPath, user: "alice");

                closeStatus.Should().NotBe(WebSocketCloseStatus.PolicyViolation,
                    "an authenticated caller passes the binary mount's identity gate");
                frame.Should().NotBeNull("the authenticated query executes");
                frame!.Type.Should().Be(BifrostMessageType.Result);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public async Task BinaryMountedBeforeAuthentication_AuthenticatedCaller_IsClosed_NeverServedAsAnonymous()
        {
            // Mount order is a load-bearing security fact (AGENTS.md, "Listener Exposure
            // Posture"): the mount's identity gate reads the principal that the
            // authentication middleware populates, and UseBifrostEndpoints()/UseBifrostQL()
            // is what adds that middleware. Mounted FIRST — the shape the H8 review found in
            // a docs example (fixed in 295cdb33) — the gate sees every caller as anonymous.
            //
            // Nothing detects that misordering, and deliberately so: a middleware cannot
            // distinguish "authentication runs later in this pipeline" from "this host runs
            // no authentication middleware at all", which is a legitimate anonymous-only
            // deployment (and from a branch-scoped UseAuthentication on another Map branch).
            // A startup or first-request diagnostic would therefore fire on valid
            // configurations. What CAN be pinned, and is what actually matters, is that the
            // misordering fails CLOSED: the caller is refused, never served as anonymous.
            //
            // Note the alias trap this guards against (protocol-adapter-security invariant 12):
            // an EMPTY user context is not a refusal, so "the context was empty" is not the
            // assertion — the connection being CLOSED with no answer frame is.
            var dbPath = Path.Combine(Path.GetTempPath(), $"binary-authgate-{Guid.NewGuid():N}.db");
            using var host = await BuildWebSocketHostAsync(dbPath, mountBinaryBeforeAuthentication: true);
            try
            {
                // Same caller that is SERVED by the correctly ordered pipeline
                // (AuthenticatedWebSocket_OnAuthRequiredEndpoint_PassesTheGate), which is
                // what makes this fact about the ORDER rather than about the credential.
                var (closeStatus, frame) = await QueryOverSocketAsync(host, SecuredSocketPath, user: "alice");

                frame.Should().BeNull(
                    "a mount placed ahead of the authentication middleware sees no principal, and must " +
                    "refuse rather than serve the caller anonymously — no answer frame may come back");
                closeStatus.Should().Be(WebSocketCloseStatus.PolicyViolation,
                    "the misordered mount closes on its auth requirement, exactly as it does for a genuinely anonymous caller");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public async Task BinaryMountPath_MultipleEndpoints_RefusesArbitraryFallback()
        {
            // The HTTP path refuses to fall back to the "first" endpoint when more than one is
            // registered (UnknownBifrostEndpointException), because "first" is nondeterministic
            // and would answer against the wrong database. The binary transport must apply the
            // same guard: with two registered GraphQL endpoints and the default (unresolved)
            // graphqlPath, executing must refuse rather than pick an arbitrary database.
            // Non-vacuous: with a single registered endpoint the same call resolves and executes
            // (BinaryMountPath_NotAGraphQlEndpoint_ResolvesSchemaFromRegisteredGraphQlEndpoint).
            await using var provider = BuildProvider(loaderPath: "/graphql/sales", secondLoaderPath: "/graphql/archive");
            var engine = provider.GetRequiredService<IBifrostEngine>();

            var context = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

            var result = await engine.ExecuteAsync(new BifrostRequest
            {
                Query = "{ __typename }",
                UserContext = new Dictionary<string, object?>(),
                RequestServices = provider,
                CancellationToken = default,
            }, "/bifrost-ws"); // unresolved mount path with >1 registered endpoint

            Messages(result).Should().Contain(m => m.Contains("more than one") && m.Contains("GraphQL endpoint"),
                "the binary transport must refuse an arbitrary cross-database fallback");
            result.Data.Should().BeNull("nothing executes when the target database is ambiguous");
        }
    }
}
