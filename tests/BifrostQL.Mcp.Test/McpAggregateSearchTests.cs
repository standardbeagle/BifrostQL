using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.IO.Pipelines;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// End-to-end tests for <c>bifrost_aggregate</c> and <c>bifrost_search</c>
    /// over the real SDK client/server pair and the real intent-executor
    /// pipeline, with the tenant and soft-delete filter transformers active and
    /// a tenant-A user context — so every aggregate value and search hit doubles
    /// as an assertion that the security seam scopes protocol-adapter traffic.
    ///
    /// Fixture data: customers(2: 'Acme Corp', 'Globex') → orders(5: three live
    /// tenant-A ['open' 10.5 + 'open' 20 + 'closed' 5], one soft-deleted
    /// tenant-A ['open' 100, name contains 'acme'], one live tenant-B ['open'
    /// 999, name contains 'acme']) → order_items(120 distinct SKUs, 8 of them
    /// containing 'acme').
    /// </summary>
    public sealed class McpAggregateSearchTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpagg_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private McpServer _server = null!;
        private Task _serverRun = null!;
        private McpClient _client = null!;
        private readonly CancellationTokenSource _serverStop = new();

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            var statements = new List<string>
            {
                "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    customer_id INTEGER NOT NULL REFERENCES customers(id),
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    total REAL NOT NULL,
                    deleted_at TEXT NULL
                )
                """,
                """
                CREATE TABLE order_items (
                    id INTEGER PRIMARY KEY,
                    order_id INTEGER NOT NULL REFERENCES orders(id),
                    sku TEXT NOT NULL
                )
                """,
                "INSERT INTO customers(id, name) VALUES (1, 'Acme Corp'), (2, 'Globex')",
                """
                INSERT INTO orders(id, customer_id, tenant_id, name, status, total, deleted_at) VALUES
                    (1, 1, 'A', 'acme order one', 'open', 10.5, NULL),
                    (2, 1, 'A', 'acme order two', 'open', 20.0, NULL),
                    (3, 1, 'A', 'plain order', 'closed', 5.0, NULL),
                    (4, 2, 'A', 'acme deleted order', 'open', 100.0, '2026-01-01'),
                    (5, 2, 'B', 'acme foreign order', 'open', 999.0, NULL)
                """,
            };
            // 120 distinct SKUs (exceeds the 100-group aggregate cap); 8 contain
            // 'acme' (exceeds the per-table search cap of 5).
            for (var i = 1; i <= 120; i++)
            {
                var sku = i <= 8 ? $"acme-part-{i:000}" : $"SKU-{i:000}";
                statements.Add($"INSERT INTO order_items(id, order_id, sku) VALUES ({i}, 1, '{sku}')");
            }
            foreach (var sql in statements)
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
                            e.Metadata = new[] { "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }" };
                            e.DisableAuth = true;
                        });
                    });
                });
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();

            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor,
                userContextProvider: () => new Dictionary<string, object?> { ["tenant_id"] = "A" });

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                serverName: "BifrostQL-aggregate-search-test");
            _server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            _serverRun = _server.RunAsync(_serverStop.Token);

            _client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()));
        }

        public async Task DisposeAsync()
        {
            await _client.DisposeAsync();
            await _serverStop.CancelAsync();
            try
            {
                await _serverRun;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path for a stream-transport session.
            }
            await _server.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
            _serverStop.Dispose();
        }

        private async Task<JsonElement> CallOk(string tool, Dictionary<string, object?> args)
        {
            var result = await _client.CallToolAsync(tool, args);
            result.IsError.Should().NotBeTrue(
                $"tool {tool} should succeed but returned: {result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text}");
            result.StructuredContent.Should().NotBeNull();
            return result.StructuredContent!.Value;
        }

        private async Task<string> CallError(string tool, Dictionary<string, object?> args)
        {
            var result = await _client.CallToolAsync(tool, args);
            result.IsError.Should().BeTrue($"tool {tool} should fail with a prompt-style error");
            return result.Content.OfType<TextContentBlock>().Single().Text;
        }

        // ---- bifrost_aggregate --------------------------------------------------

        [Fact]
        public async Task Aggregate_GroupByStatusWithCountAndSum_ScopesToTenantAndHidesSoftDeleted()
        {
            var payload = await CallOk("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["groupBy"] = new[] { "status" },
                ["measures"] = new object[]
                {
                    new Dictionary<string, object?> { ["fn"] = "count" },
                    new Dictionary<string, object?> { ["fn"] = "sum", ["column"] = "total" },
                },
            });

            payload.GetProperty("table").GetString().Should().Be("orders");
            payload.GetProperty("groupCount").GetInt32().Should().Be(2);
            var groups = payload.GetProperty("groups").EnumerateArray()
                .ToDictionary(
                    g => g.GetProperty("group").GetProperty("status").GetString()!,
                    g => g.GetProperty("measures"));

            // Tenant B's 999 and the soft-deleted 100 must never enter the sums:
            // the transformer filters constrain rows BEFORE grouping.
            groups["open"].GetProperty("count").GetInt32().Should().Be(2);
            groups["open"].GetProperty("sum_total").GetDouble().Should().Be(30.5);
            groups["closed"].GetProperty("count").GetInt32().Should().Be(1);
            groups["closed"].GetProperty("sum_total").GetDouble().Should().Be(5.0);
        }

        [Fact]
        public async Task Aggregate_NoGroupBy_ReturnsOneWholeTableGroup()
        {
            var payload = await CallOk("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["measures"] = new object[]
                {
                    new Dictionary<string, object?> { ["fn"] = "count" },
                    new Dictionary<string, object?> { ["fn"] = "avg", ["column"] = "total" },
                    new Dictionary<string, object?> { ["fn"] = "max", ["column"] = "total" },
                },
            });

            var group = payload.GetProperty("groups").EnumerateArray().Single();
            group.GetProperty("group").EnumerateObject().Should().BeEmpty();
            group.GetProperty("measures").GetProperty("count").GetInt32().Should().Be(3);
            group.GetProperty("measures").GetProperty("avg_total").GetDouble()
                .Should().BeApproximately((10.5 + 20.0 + 5.0) / 3, 1e-9);
            // max is 20, not 999 (tenant B) and not 100 (soft-deleted).
            group.GetProperty("measures").GetProperty("max_total").GetDouble().Should().Be(20.0);
        }

        [Fact]
        public async Task Aggregate_WithFilter_ConstrainsRowsBeforeGrouping()
        {
            var payload = await CallOk("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["groupBy"] = new[] { "status" },
                ["measures"] = new object[] { new Dictionary<string, object?> { ["fn"] = "count" } },
                ["filter"] = new Dictionary<string, object?>
                {
                    ["total"] = new Dictionary<string, object?> { ["_gte"] = 10 },
                },
            });

            var group = payload.GetProperty("groups").EnumerateArray().Single();
            group.GetProperty("group").GetProperty("status").GetString().Should().Be("open");
            group.GetProperty("measures").GetProperty("count").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task Aggregate_HighCardinalityGroupBy_TruncatesAtCapWithSteeringMessage()
        {
            var payload = await CallOk("bifrost_aggregate", new()
            {
                ["table"] = "order_items",
                ["groupBy"] = new[] { "sku" },
                ["measures"] = new object[] { new Dictionary<string, object?> { ["fn"] = "count" } },
            });

            payload.GetProperty("groupCount").GetInt32().Should().Be(120);
            payload.GetProperty("returnedCount").GetInt32().Should().Be(100);
            payload.GetProperty("groups").GetArrayLength().Should().Be(100);
            payload.GetProperty("message").GetString().Should()
                .Contain("120 groups").And.Contain("100").And.Contain("filter");
        }

        [Fact]
        public async Task Aggregate_SumOnNonNumericColumn_PromptsWithTheNumericColumns()
        {
            var text = await CallError("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["measures"] = new object[]
                {
                    new Dictionary<string, object?> { ["fn"] = "sum", ["column"] = "name" },
                },
            });

            text.Should().Contain("'name'").And.Contain("not numeric")
                .And.Contain("total", "the prompt must list the numeric columns to steer the agent");
        }

        [Fact]
        public async Task Aggregate_CountWithColumn_PromptsToOmitTheColumn()
        {
            var text = await CallError("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["measures"] = new object[]
                {
                    new Dictionary<string, object?> { ["fn"] = "count", ["column"] = "total" },
                },
            });

            text.Should().Contain("count").And.Contain("omit 'column'");
        }

        [Fact]
        public async Task Aggregate_UnknownGroupByColumn_PromptsWithDidYouMean()
        {
            var text = await CallError("bifrost_aggregate", new()
            {
                ["table"] = "orders",
                ["groupBy"] = new[] { "statis" },
                ["measures"] = new object[] { new Dictionary<string, object?> { ["fn"] = "count" } },
            });

            text.Should().Contain("Unknown column 'statis'").And.Contain("Did you mean 'status'?");
        }

        [Fact]
        public async Task Aggregate_MissingMeasures_Prompts()
        {
            var text = await CallError("bifrost_aggregate", new() { ["table"] = "orders" });
            text.Should().Contain("measures").And.Contain("count");
        }
    }
}
