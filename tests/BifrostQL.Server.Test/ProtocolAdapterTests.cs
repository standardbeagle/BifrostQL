using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// A request on the echo adapter's "wire": a table name, the columns to read,
    /// an optional filter, and the caller's principal as it would arrive on a real
    /// protocol (decoded token, mTLS identity, …). Deliberately trivial — the point
    /// is the path, not the codec.
    /// </summary>
    public sealed class EchoRequest
    {
        public required string Table { get; init; }
        public required IReadOnlyList<string> Columns { get; init; }
        public IReadOnlyDictionary<string, object?>? Filter { get; init; }
        public ClaimsPrincipal? Principal { get; init; }
        public string? Endpoint { get; init; }
    }

    /// <summary>
    /// Reference implementation of <see cref="IProtocolAdapter"/> with zero wire
    /// code: it proves the full hosting contract end to end. Identity is projected
    /// through <see cref="IBifrostAuthContextFactory"/> (the same seam every HTTP
    /// transport gate uses, so unmapped-OIDC-issuer fail-closed semantics hold on
    /// the adapter path too) and reads execute exclusively through
    /// <see cref="IQueryIntentExecutor"/>, so the security transformer pipeline
    /// (tenant isolation, policy read guards, …) applies unconditionally. A real
    /// adapter adds listening + codec around exactly this shape.
    /// </summary>
    public sealed class EchoProtocolAdapter : IProtocolAdapter
    {
        private readonly IQueryIntentExecutor _executor;
        private readonly IBifrostAuthContextFactory _authFactory;
        private readonly IServiceProvider _services;
        private int _started;
        private int _stopped;

        public EchoProtocolAdapter(
            IQueryIntentExecutor executor,
            IBifrostAuthContextFactory authFactory,
            IServiceProvider services)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _authFactory = authFactory ?? throw new ArgumentNullException(nameof(authFactory));
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public int StartCount => _started;
        public int StopCount => _stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped++;
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            EchoRequest request, CancellationToken cancellationToken = default)
        {
            if (_started == 0 || _stopped > 0)
                throw new InvalidOperationException("Echo adapter is not running.");

            // Project the wire identity through the shared auth seam. The HttpContext
            // here is only the carrier the factory contract requires — no HTTP request
            // exists. RequestServices is set so OIDC claim-mapper resolution (and its
            // fail-closed unmapped-issuer path) behaves exactly as on the HTTP gates.
            var identityCarrier = new DefaultHttpContext { RequestServices = _services };
            if (request.Principal is not null)
                identityCarrier.User = request.Principal;
            var userContext = _authFactory.CreateUserContext(identityCarrier);

            // Codec: translate the echo request into a programmatic query tree built
            // against the endpoint's cached model.
            var model = await _executor.GetModelAsync(request.Endpoint);
            var table = model.GetTableFromDbName(request.Table);
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in request.Columns)
                query.ScalarColumns.Add(new GqlObjectColumn(column));
            if (request.Filter is not null)
                query.Filter = TableFilter.FromObject(
                    request.Filter.ToDictionary(kv => kv.Key, kv => kv.Value), table.DbName);

            var result = await _executor.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = request.Endpoint,
            }, cancellationToken);
            return result.Rows;
        }
    }

    /// <summary>
    /// An adapter whose startup fails, standing in for a real bind/listen error
    /// (port in use, bad certificate, …). Used to prove the fail-fast lifecycle.
    /// </summary>
    public sealed class FailingStartProtocolAdapter : IProtocolAdapter
    {
        public const string FailureMessage = "protocol port bind failed";

        public Task StartAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(FailureMessage);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Slice 3 acceptance: the protocol-adapter hosting contract. The echo adapter is
    /// registered via <c>AddProtocolAdapter&lt;T&gt;</c>, started/stopped by the host's
    /// lifecycle, and its requests run the full core path — auth-factory identity
    /// projection, intent execution, tenant isolation — with zero wire code.
    /// </summary>
    public sealed class ProtocolAdapterTests : IAsyncLifetime
    {
        private const string ConnString = "Data Source=bifrost_protocol_adapter_test;Mode=Memory;Cache=Shared";
        private const string EndpointPath = "/graphql";
        private SqliteConnection _keepAlive = null!;

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(ConnString);
            await _keepAlive.OpenAsync();
            await Exec("DROP TABLE IF EXISTS orders");
            await Exec(
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL
                )
                """);
            await Exec(
                """
                INSERT INTO orders(id, tenant_id, name) VALUES
                    (1, 'tenant-a', 'a-first'),
                    (2, 'tenant-a', 'a-second'),
                    (3, 'tenant-b', 'b-only')
                """);
        }

        public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

        private async Task Exec(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<IHost> BuildHostAsync()
        {
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
                            e.ConnectionString = ConnString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = new[] { "*.orders { tenant-filter: tenant_id }" };
                            e.DisableAuth = true;
                        });
                        o.AddProtocolAdapter<EchoProtocolAdapter>();
                    });
                });
                web.Configure(_ => { });
            });
            return await builder.StartAsync();
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "test"));

        private static EchoRequest OrdersRequest(ClaimsPrincipal? principal, IReadOnlyDictionary<string, object?>? filter = null) =>
            new()
            {
                Table = "orders",
                Columns = new[] { "id", "tenant_id", "name" },
                Filter = filter,
                Principal = principal,
                Endpoint = EndpointPath,
            };

        [Fact]
        public async Task HostLifecycle_StartsAndStopsTheRegisteredAdapter()
        {
            using var host = await BuildHostAsync();
            var adapter = host.Services.GetRequiredService<EchoProtocolAdapter>();

            // BuildHostAsync already started the host, which must have started the
            // adapter through its hosted-service wrapper — exactly once.
            adapter.StartCount.Should().Be(1);
            adapter.StopCount.Should().Be(0);

            await host.StopAsync();

            adapter.StopCount.Should().Be(1);
        }

        [Fact]
        public async Task HostStartup_AdapterStartFailure_AbortsHostStart()
        {
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
                            e.ConnectionString = ConnString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.DisableAuth = true;
                        });
                        o.AddProtocolAdapter<FailingStartProtocolAdapter>();
                    });
                });
                web.Configure(_ => { });
            });

            // A bind/listen error must abort host startup — never a host that looks
            // healthy while its protocol front end silently failed to start.
            var act = () => builder.StartAsync();

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{FailingStartProtocolAdapter.FailureMessage}*");
        }

        [Fact]
        public async Task EchoAdapter_TenantPrincipal_NeverSeesOtherTenantsRows()
        {
            using var host = await BuildHostAsync();
            var adapter = host.Services.GetRequiredService<EchoProtocolAdapter>();

            // Tenant A: only its two rows, even though it asked for the whole table.
            var tenantARows = await adapter.ExecuteAsync(OrdersRequest(TenantPrincipal("user-a", "tenant-a")));
            tenantARows.Should().HaveCount(2);
            tenantARows.Select(r => (string)r["name"]!).Should().BeEquivalentTo("a-first", "a-second");

            // Tenant B on the same adapter: only its own row.
            var tenantBRows = await adapter.ExecuteAsync(OrdersRequest(TenantPrincipal("user-b", "tenant-b")));
            tenantBRows.Should().ContainSingle(r => (string)r["name"]! == "b-only");

            await host.StopAsync();
        }

        [Fact]
        public async Task EchoAdapter_CallerFilterComposesWithTenantIsolation()
        {
            using var host = await BuildHostAsync();
            var adapter = host.Services.GetRequiredService<EchoProtocolAdapter>();

            // The caller's own filter narrows within the tenant scope; it can never widen it.
            var rows = await adapter.ExecuteAsync(OrdersRequest(
                TenantPrincipal("user-a", "tenant-a"),
                new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?> { ["_eq"] = "a-second" },
                }));

            rows.Should().ContainSingle(r => (string)r["name"]! == "a-second");

            await host.StopAsync();
        }

        [Fact]
        public async Task EchoAdapter_UnauthenticatedRequest_FailsClosedOnTenantTable()
        {
            using var host = await BuildHostAsync();
            var adapter = host.Services.GetRequiredService<EchoProtocolAdapter>();

            // No principal → empty user context → the tenant transformer must refuse,
            // exactly as it does for an unauthenticated GraphQL request.
            var act = () => adapter.ExecuteAsync(OrdersRequest(principal: null));

            await act.Should().ThrowAsync<BifrostExecutionError>()
                .WithMessage("*Tenant context required*");

            await host.StopAsync();
        }
    }
}
