using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Mcp.Test.Eval;
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
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Dogfooding payoff (mcp-tools slice 7): re-runs the slice-5 eval harness over the
    /// SAME customer task twice — once with the GENERIC tools (bifrost_query +
    /// bifrost_aggregate, two calls) and once with a DECLARED domain tool
    /// (get_customer_open_orders, one call that consolidates the read and the summary) —
    /// and pins that the declared run is strictly lower on BOTH call count and response
    /// bytes. The side-by-side metrics are committed as a baseline artifact so a
    /// regression that erodes the consolidation win is caught.
    ///
    /// <para>Regenerate the baseline after an intentional tool-output change by setting
    /// <c>BIFROST_MCP_EVAL_UPDATE=1</c>.</para>
    /// </summary>
    public sealed class McpDeclaredVsGenericEvalTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpevalcmp_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in new[]
            {
                "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    customer_id INTEGER NOT NULL REFERENCES customers(id),
                    status TEXT NOT NULL,
                    total REAL NOT NULL
                )
                """,
                "INSERT INTO customers(id, name) VALUES (1,'Acme Corp'),(2,'Globex')",
                """
                INSERT INTO orders(id, customer_id, status, total) VALUES
                    (1, 1, 'open', 100.0),
                    (2, 1, 'open', 50.0),
                    (3, 1, 'closed', 30.0),
                    (4, 2, 'open', 20.0)
                """,
            })
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                {
                    e.ConnectionString = _connString;
                    e.Provider = "sqlite";
                    e.Path = EndpointPath;
                    e.DisableAuth = true;
                })));
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
        }

        [Fact]
        public async Task DeclaredDomainTool_BeatsGenericTools_OnCallsAndBytes()
        {
            var harness = new McpEvalHarness();

            var generic = await harness.RunAsync(GenericInvoker(), new[] { GenericScenario() });
            var declared = await harness.RunAsync(DeclaredInvoker(), new[] { DeclaredScenario() });

            generic.AllPassed.Should().BeTrue(generic.Scenarios.FirstOrDefault(s => !s.Passed)?.FailureReason);
            declared.AllPassed.Should().BeTrue(declared.Scenarios.FirstOrDefault(s => !s.Passed)?.FailureReason);

            // The dogfooding claim, measured: one declared tool consolidates the two
            // generic calls, and its single structured response is smaller than the two
            // generic envelopes combined.
            declared.Totals.Calls.Should().BeLessThan(generic.Totals.Calls,
                "a declared domain tool consolidates the read + summary into fewer calls");
            declared.Totals.ResponseBytes.Should().BeLessThan(generic.Totals.ResponseBytes,
                "one consolidated response carries fewer bytes than two generic-tool envelopes");

            // Commit the side-by-side comparison as a regression reference.
            var report = new
            {
                Task = "customer-1-open-orders-and-total",
                Generic = new { generic.Totals.Calls, generic.Totals.ResponseBytes },
                Declared = new { declared.Totals.Calls, declared.Totals.ResponseBytes },
                CallsSaved = generic.Totals.Calls - declared.Totals.Calls,
                BytesSaved = generic.Totals.ResponseBytes - declared.Totals.ResponseBytes,
            };
            var actual = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var baselinePath = BaselinePath();
            if (Environment.GetEnvironmentVariable("BIFROST_MCP_EVAL_UPDATE") == "1" || !File.Exists(baselinePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
                await File.WriteAllTextAsync(baselinePath, actual);
            }
            else
            {
                var expected = await File.ReadAllTextAsync(baselinePath);
                Normalize(actual).Should().Be(Normalize(expected),
                    "the declared-vs-generic comparison must match the committed baseline; set BIFROST_MCP_EVAL_UPDATE=1 to regenerate");
            }
        }

        // ---- the same task, two ways ------------------------------------------

        private static McpEvalScenario GenericScenario() => new()
        {
            Name = "generic-customer-open-orders",
            Steps = new[]
            {
                new McpEvalStep
                {
                    Tool = "bifrost_query",
                    Args = new Dictionary<string, object?>
                    {
                        ["table"] = "orders",
                        ["fields"] = new[] { "id", "total" },
                        ["filter"] = new Dictionary<string, object?>
                        {
                            ["customer_id"] = new Dictionary<string, object?> { ["_eq"] = 1 },
                            ["status"] = new Dictionary<string, object?> { ["_eq"] = "open" },
                        },
                    },
                    Assert = p => p.GetProperty("returnedCount").GetInt32() == 2 ? null : "expected 2 open orders",
                },
                new McpEvalStep
                {
                    Tool = "bifrost_aggregate",
                    Args = new Dictionary<string, object?>
                    {
                        ["table"] = "orders",
                        ["measures"] = new[]
                        {
                            new Dictionary<string, object?> { ["fn"] = "sum", ["column"] = "total" },
                        },
                        ["filter"] = new Dictionary<string, object?>
                        {
                            ["customer_id"] = new Dictionary<string, object?> { ["_eq"] = 1 },
                            ["status"] = new Dictionary<string, object?> { ["_eq"] = "open" },
                        },
                    },
                    Assert = p => p.GetProperty("groups").EnumerateArray().Single()
                        .GetProperty("measures").GetProperty("sum_total").GetDouble() == 150.0
                        ? null : "expected open-order total of 150",
                },
            },
        };

        private static McpEvalScenario DeclaredScenario() => new()
        {
            Name = "declared-customer-open-orders",
            Steps = new[]
            {
                new McpEvalStep
                {
                    Tool = "get_customer_open_orders",
                    Args = new Dictionary<string, object?> { ["customerId"] = 1 },
                    Assert = p =>
                    {
                        var orders = p.GetProperty("data").GetProperty("openOrders");
                        var total = orders.EnumerateArray().Sum(o => o.GetProperty("total").GetDouble());
                        return orders.GetArrayLength() == 2 && total == 150.0
                            ? null : "expected 2 open orders totaling 150 in one call";
                    },
                },
            },
        };

        private const string DeclaredDocument = """
            {
              "version": 1,
              "tools": [
                {
                  "name": "get_customer_open_orders",
                  "description": "One customer with their open orders and the open-order total, in a single call.",
                  "params": { "customerId": { "type": "id", "table": "main.customers", "description": "customer id" } },
                  "root": { "table": "main.customers", "byId": "customerId", "fields": ["id", "name"] },
                  "include": [
                    {
                      "relation": "orders", "as": "openOrders", "fields": ["id", "total"],
                      "filter": { "status": { "_eq": "open" } }, "sort": "id", "limit": 50
                    }
                  ]
                }
              ]
            }
            """;

        private McpToolInvoker GenericInvoker() => BuildInvoker(declarativeTools: null);

        private McpToolInvoker DeclaredInvoker()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(DeclaredDocument));
            var document = new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(stream, "declared-eval")).Load();
            return BuildInvoker(document);
        }

        private McpToolInvoker BuildInvoker(DeclarativeToolDocument? declarativeTools) => async (tool, args) =>
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor, EndpointPath,
                userContextProvider: () => new Dictionary<string, object?>(),
                declarativeTools: declarativeTools);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-eval-cmp");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try
            {
                var result = await client.CallToolAsync(tool, new Dictionary<string, object?>(args));
                var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                return new McpEvalCallResult(result.IsError == true, text, result.StructuredContent);
            }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        };

        private static string BaselinePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.Mcp.Test.csproj")))
                dir = dir.Parent;
            if (dir is null)
                throw new InvalidOperationException("Could not locate the BifrostQL.Mcp.Test project directory.");
            return Path.Combine(dir.FullName, "Eval", "mcp-eval-declared-vs-generic.json");
        }

        private static string Normalize(string json) => json.Replace("\r\n", "\n").Trim();
    }
}
