using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// The MCP auth matrix across BOTH transports — Streamable HTTP (this slice's new
    /// front door) and stdio (the per-call provider seam) — proving the same three
    /// outcomes on each: a valid bearer scopes reads to the caller's tenant, a missing
    /// bearer fails closed, and a token from an unmapped OIDC issuer fails closed as a
    /// sanitized MCP error (never an empty/anonymous context, never cross-tenant rows).
    ///
    /// <para>The HTTP transport is exercised end to end: a real <see cref="McpClient"/>
    /// over the TestServer's HttpClient, JSON-RPC over the wire, with the bearer carried
    /// in the <c>Authorization</c> header of the session-initiating request. Identity is
    /// derived only through slice C's credential seam + the shared auth factory — this
    /// project contains no bespoke claim mapping.</para>
    /// </summary>
    public sealed class McpHttpTransportTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";
        private const string ValidToken = "tenant-a-token";
        private const string UnmappedIssuerToken = "unmapped-issuer-token";

        private readonly string _connString =
            $"Data Source=mcphttp_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        // Bearer validation is the host's job (slice C). The adapter reads no claims; it
        // hands the whole principal to the factory. A valid token → tenant-A principal;
        // the unmapped-issuer token → a principal carrying an iss no mapper covers.
        // Revocation is modelled the way a real host models it: the validator stops
        // resolving the token. Nothing about the SESSION changes, which is exactly the
        // condition the per-request re-validation exists to catch.
        private static readonly HashSet<string> RevokedTokens = new(StringComparer.Ordinal);

        private static readonly McpAuthOptions Auth = new()
        {
            Mode = McpAuthMode.Bearer,
            EnableWrites = false,
            ValidateBearerToken = token => RevokedTokens.Contains(token) ? null : token switch
            {
                ValidToken => TenantPrincipal("user-a", "tenant-a"),
                UnmappedIssuerToken => UnmappedIssuerPrincipal(),
                _ => null,
            },
        };

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in new[]
            {
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL
                )
                """,
                "INSERT INTO orders(id, tenant_id, name) VALUES (1,'tenant-a','a-first'),(2,'tenant-a','a-second'),(3,'tenant-b','b-only')",
            })
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = new[] { "*.orders { tenant-filter: tenant_id }" };
                            e.DisableAuth = true;
                        });
                    });
                    // An OIDC mapper registry (empty) makes the unmapped-issuer path
                    // reachable: with a registry present but no mapper for the token's
                    // issuer, the shared factory fails closed instead of degrading to
                    // the local claim path.
                    services.AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()));
                    services.AddRouting();
                    services.AddBifrostMcpHttp(Auth, EndpointPath);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapBifrostMcp("/mcp"));
                });
            });
            _host = await builder.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
        }

        // ---- HTTP transport --------------------------------------------------

        [Fact]
        public async Task Http_ValidBearer_ScopesReadToTheCallersTenant()
        {
            var rows = await HttpQueryOrdersAsync($"Bearer {ValidToken}");
            rows.GetProperty("totalCount").GetInt32().Should().Be(2);
            rows.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString())
                .Should().BeEquivalentTo("a-first", "a-second");
        }

        [Fact]
        public async Task Http_MissingBearer_FailsClosedOnTenantTable()
        {
            var error = await HttpQueryOrdersErrorAsync(authorization: null);
            // Fail-closed is the fact; the raw TenantFilterTransformer text is NOT part of it.
            // That message names 'tenant_id' and the qualified table, so it is sanitized to the
            // adapter's stable code before it reaches the wire (invariant 3).
            error.Should().Contain("access_denied");
            error.Should().NotContain("tenant_id").And.NotContain("main.orders");
        }

        [Fact]
        public async Task Http_UnmappedIssuer_FailsClosedAsSanitizedError_NeverAnonymous()
        {
            var error = await HttpQueryOrdersErrorAsync($"Bearer {UnmappedIssuerToken}");
            // Sanitized (invariant 3): the wire never names the issuer, and the read is
            // rejected — not degraded to an empty/anonymous success.
            error.Should().Contain("Authentication failed");
            error.Should().NotContain("unmapped.example");
        }

        // ---- identity is re-established per REQUEST, not frozen at initialize ----

        /// <summary>
        /// Identity used to be resolved once at the session's initialize request and
        /// frozen: a token that expired or was revoked kept full access until the client
        /// disconnected. Every existing HTTP test opened a session and made ONE call, so
        /// none of them could manifest that — this one makes a call, revokes the token,
        /// and calls again on the SAME session.
        /// </summary>
        [Fact]
        public async Task Http_TokenRevokedAfterInitialize_NextRequestOnTheSameSessionFailsClosed()
        {
            var client = await ConnectHttpAsync($"Bearer {ValidToken}");
            try
            {
                var before = await client.CallToolAsync("bifrost_query",
                    new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });
                before.IsError.Should().NotBeTrue(
                    before.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

                RevokedTokens.Add(ValidToken);

                var after = await client.CallToolAsync("bifrost_query",
                    new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });
                after.IsError.Should().BeTrue(
                    "a revoked credential must lose the session's access at the next request, " +
                    "not at disconnect");
                after.Content.OfType<TextContentBlock>().Single().Text
                    .Should().Contain("Authentication failed");
            }
            finally
            {
                RevokedTokens.Remove(ValidToken);
                await client.DisposeAsync();
            }
        }

        /// <summary>
        /// The session id alone used to be sufficient after initialize — no Authorization
        /// header was ever looked at again. The session is now bound to the credential it
        /// was opened with, so a request that drops or swaps the credential fails closed.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("Bearer some-other-token")]
        public async Task SessionBoundToCredential_RequestWithMissingOrDifferentCredential_FailsClosed(
            string? authorization)
        {
            var httpContext = new DefaultHttpContext { RequestServices = _host.Services };
            if (authorization is not null)
                httpContext.Request.Headers.Authorization = authorization;
            var accessor = new HttpContextAccessor { HttpContext = httpContext };
            var identity = new BifrostMcpHttpExtensions.McpHttpSessionIdentity(Auth, accessor, ValidToken);

            var act = () => identity.RevalidateAsync(CancellationToken.None).AsTask();

            await act.Should().ThrowAsync<McpIdentityException>();
        }

        [Fact]
        public async Task SessionBoundToCredential_SameCredential_ProjectsTheCallersContext()
        {
            var httpContext = new DefaultHttpContext { RequestServices = _host.Services };
            httpContext.Request.Headers.Authorization = $"Bearer {ValidToken}";
            var accessor = new HttpContextAccessor { HttpContext = httpContext };
            var identity = new BifrostMcpHttpExtensions.McpHttpSessionIdentity(Auth, accessor, ValidToken);

            await identity.RevalidateAsync(CancellationToken.None);

            identity.Current()["tenant_id"].Should().Be("tenant-a");
        }

        // ---- stdio transport (same matrix through the per-call provider) ------

        [Fact]
        public async Task Stdio_ValidBearer_ScopesReadToTheCallersTenant()
        {
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, StdioBearer(ValidToken));

            var result = await StdioQueryOrdersAsync(provider);
            result.IsError.Should().NotBeTrue();
            result.StructuredContent!.Value.GetProperty("totalCount").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task Stdio_MissingBearer_FailsClosed()
        {
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, StdioBearer("no-such-token"));

            var result = await StdioQueryOrdersAsync(provider);
            result.IsError.Should().BeTrue();
            // Same contract as the HTTP transport: fail closed, sanitized code, no identifiers.
            var text = result.Content.OfType<TextContentBlock>().Single().Text;
            text.Should().Contain("access_denied");
            text.Should().NotContain("tenant_id").And.NotContain("main.orders");
        }

        [Fact]
        public async Task Stdio_UnmappedIssuer_FailsClosedAsSanitizedError()
        {
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, StdioBearer(UnmappedIssuerToken));

            var result = await StdioQueryOrdersAsync(provider);
            result.IsError.Should().BeTrue();
            var text = result.Content.OfType<TextContentBlock>().Single().Text;
            text.Should().Contain("Authentication failed");
            text.Should().NotContain("unmapped.example");
        }

        // ---- helpers ---------------------------------------------------------

        private async Task<McpClient> ConnectHttpAsync(string? authorization)
        {
            var httpClient = _host.GetTestClient();
            var headers = new Dictionary<string, string>();
            if (authorization is not null)
                headers["Authorization"] = authorization;

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers,
            }, httpClient);
            return await McpClient.CreateAsync(transport);
        }

        private async Task<System.Text.Json.JsonElement> HttpQueryOrdersAsync(string authorization)
        {
            var client = await ConnectHttpAsync(authorization);
            try
            {
                var result = await client.CallToolAsync("bifrost_query",
                    new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });
                result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                return result.StructuredContent!.Value;
            }
            finally { await client.DisposeAsync(); }
        }

        private async Task<string> HttpQueryOrdersErrorAsync(string? authorization)
        {
            var client = await ConnectHttpAsync(authorization);
            try
            {
                var result = await client.CallToolAsync("bifrost_query",
                    new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });
                result.IsError.Should().BeTrue("the read must be rejected, never an empty/anonymous success");
                return result.Content.OfType<TextContentBlock>().Single().Text;
            }
            finally { await client.DisposeAsync(); }
        }

        private async Task<CallToolResult> StdioQueryOrdersAsync(Func<IDictionary<string, object?>> provider)
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(executor, EndpointPath, provider);

            var clientToServer = new System.IO.Pipelines.Pipe();
            var serverToClient = new System.IO.Pipelines.Pipe();
            var transport = new ModelContextProtocol.Server.StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-stdio-matrix");
            await using var server = ModelContextProtocol.Server.McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try
            {
                return await client.CallToolAsync("bifrost_query",
                    new Dictionary<string, object?> { ["table"] = "orders", ["detail"] = "full" });
            }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }

        private static McpAuthOptions StdioBearer(string token) => new()
        {
            Mode = McpAuthMode.Bearer,
            ValidateBearerToken = Auth.ValidateBearerToken,
            CredentialSource = () => $"Bearer {token}" is var h ? McpCredentialSources.ExtractBearerToken(h) : null,
        };

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "test"));

        private static ClaimsPrincipal UnmappedIssuerPrincipal() =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-x"),
                new Claim("iss", "https://unmapped.example/"),
            }, authenticationType: "oidc"));
    }
}
