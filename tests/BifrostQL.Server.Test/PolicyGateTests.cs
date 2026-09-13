using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
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

namespace BifrostQL.Server.Test;

/// <summary>
/// S12: <see cref="IPolicyGate"/> lets an application endpoint ask the SAME policy evaluator
/// the GraphQL door runs. The two evaluator-less facts at the top pin the gate's own answers;
/// everything below drives one real host — <c>AddBifrostQL</c> over a SQLite model carrying
/// policy metadata, a registered <see cref="IGrantResolver"/>, and a minimal-API
/// <c>POST /payments</c> beside the <c>/graphql</c> mount — so the gate is proven through DI,
/// through <c>BifrostIdentityGate.Project</c>, and against the wire shape the GraphQL mount
/// gives the same caller.
/// </summary>
public sealed class PolicyGateTests
{
    [Fact]
    public void UnknownTableIsDeniedAndRequireUsesGenericAccessError()
    {
        var model = new DbModel
        {
            Tables = Array.Empty<IDbTable>(),
            StoredProcedures = Array.Empty<DbStoredProcedure>(),
            Metadata = new Dictionary<string, object?>()
        };
        var gate = new PolicyGate(model, new AppIdentity("user", "test"));

        gate.CanAct("public.payments", PolicyAction.Create).Should().Be(PolicyDecision.Deny);
        var error = Assert.Throws<BifrostExecutionError>(() => gate.Require("public.payments", PolicyAction.Create));
        error.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        error.Message.Should().Be(PolicyDecision.Deny.Reason);
    }

    [Fact]
    public void RefusedGateDeniesEveryQuestionWithTheGenericAccessError()
    {
        // The gate a caller without a projectable identity receives from DI: it must answer
        // every question closed and refuse with the same wire shape the data path uses, so an
        // anonymous caller of an app endpoint cannot learn anything a policy-bearing table hides.
        var gate = PolicyGate.Refused;

        gate.CanAct("public.orders", PolicyAction.Read).Should().Be(PolicyDecision.Deny);
        gate.CanReadColumn("public.orders", "total").Should().Be(PolicyDecision.Deny);
        gate.CanWriteColumn("public.orders", "total").Should().Be(PolicyDecision.Deny);
        var error = Assert.Throws<BifrostExecutionError>(() => gate.Require("public.orders", PolicyAction.Read));
        error.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        error.Message.Should().Be(PolicyDecision.Deny.Reason);
    }

    // ---------------------------------------------------------------------------------
    // Host-level proof: AddBifrostQL + IGrantResolver + POST /payments beside /graphql.
    // ---------------------------------------------------------------------------------

    private const string GraphQlPath = "/graphql";
    private const string ClaimsHeader = "X-Test-Claims";
    private const string Table = "main.payments";
    private const string AccessDeniedMessage = "Access denied by authorization policy.";

    /// <summary>The one caller id the grant resolver hands grants to; every other id gets none.</summary>
    private const string GrantedUserId = "user-granted";
    private const string PlainUserId = "user-plain";
    private const string AdminUserId = "user-admin";

    /// <summary>
    /// payments: read is open to any listed caller; create needs the <c>payments.create</c>
    /// grant; update needs <c>projects.manage</c>; delete is absent from the allow-list (D7).
    /// cost_rate is write-gated on <c>projects.manage</c>; margin is read-gated on
    /// <c>rates.view</c>. All three grants come ONLY from the registered IGrantResolver —
    /// no caller carries them as a role claim.
    /// </summary>
    private static readonly string[] PolicyMetadata =
    {
        "main.payments { policy-actions: read, create[payments.create], update[projects.manage] }",
        "main.payments.cost_rate { write-requires: projects.manage }",
        "main.payments.margin { read-requires: rates.view }",
    };

    private static readonly string[] ResolverGrants = { "payments.create", "projects.manage", "rates.view" };

    private sealed class GateHost : IAsyncDisposable
    {
        public IHost Inner { get; }
        public HttpClient Client { get; }
        public IServiceCollection Services { get; }
        private readonly string _dbPath;

        private GateHost(IHost inner, IServiceCollection services, string dbPath)
        {
            Inner = inner;
            Services = services;
            Client = inner.GetTestClient();
            _dbPath = dbPath;
        }

        public static async Task<GateHost> StartAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"s12-policy-gate-{Guid.NewGuid():N}.db");
            CreateDatabase(dbPath);

            var configValues = new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = GraphQlPath,
                ["Bifrost:Playground"] = "/graphiql",
            };
            for (var i = 0; i < PolicyMetadata.Length; i++)
                configValues[$"Bifrost:Metadata:{i}"] = PolicyMetadata[i];
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

            IServiceCollection captured = null!;
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddBifrostQL(o => o
                        .BindConfiguration(config.GetSection("Bifrost"))
                        .BindConnectionString($"Data Source={dbPath}")
                        .BindProvider("sqlite"));
                    services.AddBifrostGrantResolver((identity, sp, ct) =>
                        new ValueTask<IReadOnlyCollection<string>>(
                            identity.Id == GrantedUserId ? ResolverGrants : Array.Empty<string>()));
                    captured = services;
                });
                web.Configure(app =>
                {
                    app.Use(ClaimsMiddleware);
                    app.Use(MapAccessDeniedTo403);
                    app.UseBifrostQL();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // The documented shape (authorization guide, "and your own endpoints").
                        endpoints.MapPost("/payments", (IPolicyGate policy) =>
                        {
                            policy.Require(Table, PolicyAction.Create);
                            return Results.Ok();
                        });

                        // Probes: every answer the gate gives, read through the real DI gate
                        // inside a real request so the assertions run against host wiring.
                        endpoints.MapGet("/probe/can-act", (IPolicyGate policy, string table, string action) =>
                            Results.Json(new { allowed = policy.CanAct(table, Enum.Parse<PolicyAction>(action, true)).Allowed }));
                        endpoints.MapGet("/probe/can-write", (IPolicyGate policy, string table, string column) =>
                            Results.Json(new { allowed = policy.CanWriteColumn(table, column).Allowed }));
                        endpoints.MapGet("/probe/can-read", (IPolicyGate policy, string table, string column) =>
                            Results.Json(new { allowed = policy.CanReadColumn(table, column).Allowed }));
                        endpoints.MapGet("/probe/require", (IPolicyGate policy, string table, string action) =>
                        {
                            try
                            {
                                policy.Require(table, Enum.Parse<PolicyAction>(action, true));
                                return Results.Json(new { threw = false, code = (string?)null, message = (string?)null });
                            }
                            catch (BifrostExecutionError ex)
                            {
                                return Results.Json(new { threw = true, code = ex.ErrorCode, message = ex.Message });
                            }
                        });
                        endpoints.MapGet("/probe/refused", (IPolicyGate policy) =>
                            Results.Json(new { refused = ReferenceEquals(policy, PolicyGate.Refused) }));
                    });
                });
            });

            var host = await builder.StartAsync();
            return new GateHost(host, captured, dbPath);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Inner.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            await Task.CompletedTask;
        }

        internal static void CreateDatabase(string dbPath)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE payments (id INTEGER PRIMARY KEY AUTOINCREMENT, amount REAL NOT NULL, cost_rate REAL NULL, margin REAL NULL);" +
                "INSERT INTO payments (amount, cost_rate, margin) VALUES (10.0, 1.5, 0.2);";
            cmd.ExecuteNonQuery();
        }

        internal static async Task ClaimsMiddleware(HttpContext context, Func<Task> next)
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

        /// <summary>
        /// The host-side mapping an app endpoint needs: the gate's ACCESS_DENIED becomes a 403
        /// with an empty body — no policy detail, no exception text — the way the GraphQL
        /// mount answers the same refusal inside its error envelope.
        /// </summary>
        private static async Task MapAccessDeniedTo403(HttpContext context, Func<Task> next)
        {
            try
            {
                await next();
            }
            catch (BifrostExecutionError ex) when (ex.ErrorCode == BifrostExecutionError.AccessDeniedCode)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? userId, string role = "clerk", string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (userId is not null)
        {
            var claims = new[]
            {
                new[] { ClaimTypes.NameIdentifier, userId },
                new[] { ClaimTypes.Role, role },
            };
            request.Headers.Add(ClaimsHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)));
        }
        return request;
    }

    private static HttpRequestMessage GraphQl(string query, string? userId, string role = "clerk")
        => Request(HttpMethod.Post, GraphQlPath, userId, role, JsonSerializer.Serialize(new { query }));

    private static HttpRequestMessage RestPayment(string? userId, string role = "clerk")
        => Request(HttpMethod.Post, "/payments", userId, role, "{}");

    private static async Task<JsonElement> ProbeAsync(HttpClient client, string path, string? userId, string role = "clerk")
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, path, userId, role));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"probe {path} answered: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<bool> AllowedAsync(HttpClient client, string path, string? userId, string role = "clerk")
        => (await ProbeAsync(client, path, userId, role)).GetProperty("allowed").GetBoolean();

    private static async Task<List<string>> GraphQlErrorsAsync(HttpClient client, string query, string? userId, string role = "clerk")
    {
        using var response = await client.SendAsync(GraphQl(query, userId, role));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GraphQL responses are always HTTP 200");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = new List<string>();
        if (doc.RootElement.TryGetProperty("errors", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var error in array.EnumerateArray())
                errors.Add(error.GetProperty("message").GetString() ?? string.Empty);
        return errors;
    }

    private const string InsertPayment = "mutation { payments(insert: { amount: 5.0 }) }";

    // ---- 1. Both doors, same caller ----

    [Fact]
    public async Task BothDoors_NonAdminWithoutGrant_RestIs403EmptyBody_GraphQlIsGenericDeny()
    {
        await using var host = await GateHost.StartAsync();

        using var rest = await host.Client.SendAsync(RestPayment(PlainUserId));
        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rest.Content.ReadAsStringAsync()).Should().BeEmpty("a refused app endpoint carries no policy detail");

        var errors = await GraphQlErrorsAsync(host.Client, InsertPayment, PlainUserId);
        errors.Should().ContainSingle().Which.Should().Be(AccessDeniedMessage);
    }

    [Fact]
    public async Task BothDoors_CallerHoldingTheGrant_Is2xxOnBoth()
    {
        await using var host = await GateHost.StartAsync();

        using var rest = await host.Client.SendAsync(RestPayment(GrantedUserId));
        rest.StatusCode.Should().Be(HttpStatusCode.OK);

        var errors = await GraphQlErrorsAsync(host.Client, InsertPayment, GrantedUserId);
        errors.Should().BeEmpty("the same grant that opened the REST door opens the GraphQL insert");
    }

    // ---- 2. CanAct on a grant-gated action follows the IGrantResolver ----

    [Fact]
    public async Task CanAct_GrantGatedUpdate_AllowedOnlyWhenTheResolverYieldsTheGrant()
    {
        await using var host = await GateHost.StartAsync();
        const string probe = "/probe/can-act?table=" + Table + "&action=update";

        (await AllowedAsync(host.Client, probe, GrantedUserId)).Should().BeTrue("the resolver yields projects.manage for this id");
        (await AllowedAsync(host.Client, probe, PlainUserId)).Should().BeFalse("same role, no grant from the resolver");
    }

    // ---- 3. Column answers ----

    [Fact]
    public async Task CanWriteColumn_WriteRequiresColumn_DenyWithoutGrant_AllowWithIt()
    {
        await using var host = await GateHost.StartAsync();
        const string probe = "/probe/can-write?table=" + Table + "&column=cost_rate";

        (await AllowedAsync(host.Client, probe, PlainUserId)).Should().BeFalse();
        (await AllowedAsync(host.Client, probe, GrantedUserId)).Should().BeTrue();
        (await AllowedAsync(host.Client, "/probe/can-write?table=" + Table + "&column=amount", PlainUserId))
            .Should().BeTrue("an ungated column stays writable for a listed caller");
    }

    [Fact]
    public async Task CanReadColumn_ReadRequiresColumn_DenyAgreesWithDbSchemaReadable()
    {
        await using var host = await GateHost.StartAsync();
        const string probe = "/probe/can-read?table=" + Table + "&column=margin";

        (await AllowedAsync(host.Client, probe, PlainUserId)).Should().BeFalse("the caller lacks rates.view");
        (await AllowedAsync(host.Client, probe, GrantedUserId)).Should().BeTrue();

        // The GraphQL door's own answer for the same caller and column.
        using var response = await host.Client.SendAsync(GraphQl(
            "{ _dbSchema { dbName columns { dbName readable } } }", PlainUserId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payments = doc.RootElement.GetProperty("data").GetProperty("_dbSchema").EnumerateArray()
            .Single(t => t.GetProperty("dbName").GetString() == "payments");
        var columns = payments.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("dbName").GetString()!, c => c.GetProperty("readable").GetBoolean());
        columns["margin"].Should().BeFalse("_dbSchema.readable is the GraphQL door's word on the same question");
        columns["amount"].Should().BeTrue();
    }

    // ---- 4. Require's error shape ----

    [Fact]
    public async Task Require_Throws_AccessDeniedCode_GenericMessage_NeverNamingTheTable()
    {
        await using var host = await GateHost.StartAsync();

        var result = await ProbeAsync(host.Client, "/probe/require?table=" + Table + "&action=update", PlainUserId);
        result.GetProperty("threw").GetBoolean().Should().BeTrue();
        result.GetProperty("code").GetString().Should().Be(BifrostExecutionError.AccessDeniedCode);
        result.GetProperty("message").GetString().Should().Be(AccessDeniedMessage);
        result.GetProperty("message").GetString().Should().NotContainAny("payments", "main", "update");

        var granted = await ProbeAsync(host.Client, "/probe/require?table=" + Table + "&action=update", GrantedUserId);
        granted.GetProperty("threw").GetBoolean().Should().BeFalse();
    }

    // ---- 5. Unknown table and admin parity (D7) ----

    [Fact]
    public async Task UnknownQualifiedTable_IsDeny_WithoutAnExceptionLeak()
    {
        await using var host = await GateHost.StartAsync();

        (await AllowedAsync(host.Client, "/probe/can-act?table=main.no_such_table&action=read", GrantedUserId)).Should().BeFalse();
        (await AllowedAsync(host.Client, "/probe/can-act?table=nodot&action=read", GrantedUserId)).Should().BeFalse();
        (await AllowedAsync(host.Client, "/probe/can-read?table=main.no_such_table&column=x", AdminUserId, "admin")).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_AllowedForAListedAction_DeniedForAnActionAbsentFromTheAllowList()
    {
        await using var host = await GateHost.StartAsync();

        (await AllowedAsync(host.Client, "/probe/can-act?table=" + Table + "&action=update", AdminUserId, "admin"))
            .Should().BeTrue("an admin bypasses the grant on a LISTED action without holding it");
        (await AllowedAsync(host.Client, "/probe/can-act?table=" + Table + "&action=delete", AdminUserId, "admin"))
            .Should().BeFalse("D7: delete is absent from policy-actions, so the admin is refused too");

        // The GraphQL door says the same thing about the same caller.
        var errors = await GraphQlErrorsAsync(host.Client, "mutation { payments(delete: { id: 1 }) }", AdminUserId, "admin");
        errors.Should().ContainSingle().Which.Should().Be(AccessDeniedMessage);
    }

    // ---- 6. DI lifetime, no-HttpContext resolution, anonymous caller ----

    [Fact]
    public async Task IPolicyGate_IsRegisteredScoped_ByAddBifrostQL()
    {
        await using var host = await GateHost.StartAsync();

        var descriptor = host.Services.Single(d => d.ServiceType == typeof(IPolicyGate));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task IPolicyGate_ResolvedWithoutAnHttpContext_ThrowsAClearInvalidOperation()
    {
        await using var host = await GateHost.StartAsync();
        using var scope = host.Inner.Services.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IPolicyGate>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*HttpContext*");
    }

    [Fact]
    public async Task AnonymousRequest_GetsTheRefusedGate_AndIsDeniedEverything()
    {
        await using var host = await GateHost.StartAsync();

        (await ProbeAsync(host.Client, "/probe/refused", userId: null)).GetProperty("refused").GetBoolean()
            .Should().BeTrue("a caller the identity gate cannot project gets PolicyGate.Refused");
        (await AllowedAsync(host.Client, "/probe/can-act?table=" + Table + "&action=read", userId: null)).Should().BeFalse();
        (await AllowedAsync(host.Client, "/probe/can-read?table=" + Table + "&column=amount", userId: null)).Should().BeFalse();

        using var rest = await host.Client.SendAsync(RestPayment(userId: null));
        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rest.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    // ---- 7. An unknown column on a known table fails closed ----

    [Fact]
    public async Task UnknownColumnOnAPolicyBearingTable_IsDeny_ForReadAndWrite()
    {
        // Pre-fix: PolicyGate.ResolveColumn fell back to the raw name when it matched no
        // column, and a name the policy never mentions is neither required nor denied, so a
        // mistyped column answered Allow. The caller here holds EVERY grant, so a Deny can
        // only come from the column being unknown.
        await using var host = await GateHost.StartAsync();

        (await AllowedAsync(host.Client, "/probe/can-read?table=" + Table + "&column=no_such_column", GrantedUserId))
            .Should().BeFalse("a column the model does not have has no policy to allow it");
        (await AllowedAsync(host.Client, "/probe/can-write?table=" + Table + "&column=no_such_column", GrantedUserId))
            .Should().BeFalse("a column the model does not have has no policy to allow it");
        (await AllowedAsync(host.Client, "/probe/can-read?table=" + Table + "&column=", GrantedUserId))
            .Should().BeFalse("an empty column name is not a column");
    }

    // ---- 8. More than one registered endpoint: the gate refuses to guess, as the mount does ----

    private sealed class MultiEndpointHost : IAsyncDisposable
    {
        public HttpClient Client { get; }
        private readonly IHost _inner;
        private readonly string[] _dbPaths;

        private MultiEndpointHost(IHost inner, string[] dbPaths)
        {
            _inner = inner;
            _dbPaths = dbPaths;
            Client = inner.GetTestClient();
        }

        /// <summary>
        /// An AddBifrostEndpoints host with <paramref name="endpointCount"/> GraphQL mounts, each
        /// over its own SQLite file, plus a probe that reports whether IPolicyGate resolves for
        /// a request whose path is none of the mounts.
        /// </summary>
        public static async Task<MultiEndpointHost> StartAsync(int endpointCount)
        {
            var dbPaths = Enumerable.Range(0, endpointCount)
                .Select(i => Path.Combine(Path.GetTempPath(), $"s12-multi-{i}-{Guid.NewGuid():N}.db"))
                .ToArray();
            foreach (var dbPath in dbPaths)
                GateHost.CreateDatabase(dbPath);

            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddBifrostEndpoints(o =>
                    {
                        for (var i = 0; i < dbPaths.Length; i++)
                        {
                            var dbPath = dbPaths[i];
                            var index = i;
                            o.AddEndpoint(e =>
                            {
                                e.ConnectionString = $"Data Source={dbPath}";
                                e.Provider = "sqlite";
                                e.Path = index == 0 ? GraphQlPath : $"{GraphQlPath}{index}";
                                e.PlaygroundPath = $"/graphiql{index}";
                                e.DisableAuth = true;
                                e.Metadata = PolicyMetadata;
                            });
                        }
                    });
                });
                web.Configure(app =>
                {
                    app.Use(GateHost.ClaimsMiddleware);
                    app.UseBifrostEndpoints();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/probe/resolve", (IServiceProvider sp) =>
                        {
                            try
                            {
                                var gate = sp.GetRequiredService<IPolicyGate>();
                                return Results.Json(new
                                {
                                    resolved = true,
                                    message = (string?)null,
                                    readAllowed = gate.CanAct(Table, PolicyAction.Read).Allowed,
                                });
                            }
                            catch (InvalidOperationException ex)
                            {
                                return Results.Json(new { resolved = false, message = ex.Message, readAllowed = false });
                            }
                        });
                    });
                });
            });

            return new MultiEndpointHost(await builder.StartAsync(), dbPaths);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _inner.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var dbPath in _dbPaths)
                if (File.Exists(dbPath)) File.Delete(dbPath);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OneRegisteredEndpoint_GateResolvesThatModel()
    {
        await using var host = await MultiEndpointHost.StartAsync(endpointCount: 1);

        var result = await ProbeAsync(host.Client, "/probe/resolve", GrantedUserId);
        result.GetProperty("resolved").GetBoolean().Should().BeTrue();
        result.GetProperty("readAllowed").GetBoolean().Should().BeTrue("read is listed for payments in the one model");
    }

    [Fact]
    public async Task TwoRegisteredEndpoints_GateRefusesToGuess_WithAClearMessage()
    {
        // The GraphQL mount throws UnknownBifrostEndpointException rather than serve the
        // first database for an unmatched path when more than one endpoint is registered.
        // Pre-fix the gate read PathCache.GetFirstValueAsync unconditionally, so an app
        // endpoint was answered from whichever database happened to be registered first.
        await using var host = await MultiEndpointHost.StartAsync(endpointCount: 2);

        var result = await ProbeAsync(host.Client, "/probe/resolve", GrantedUserId);
        result.GetProperty("resolved").GetBoolean().Should().BeFalse(
            "with two registered endpoints the gate cannot know which database the caller means");
        result.GetProperty("message").GetString().Should().Contain("more than one");
    }
}
