using System.IO.Pipelines;
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
    /// Runs the no-live-LLM eval harness against the sample SQLite schema over the real
    /// MCP tool wire, checks the golden assertions for each scripted task, and pins the
    /// recorded metrics (response chars/bytes, call counts, error rate) against a
    /// committed baseline artifact. The harness is transport-decoupled (it takes an
    /// <see cref="McpToolInvoker"/>), so slice 7 of the declarative-tools epic re-runs
    /// exactly these scenarios and metric definitions against its own tool layer.
    ///
    /// <para>Regenerate the baseline after an intentional tool-output change by setting
    /// <c>BIFROST_MCP_EVAL_UPDATE=1</c>; otherwise the committed baseline is the
    /// regression reference.</para>
    /// </summary>
    public sealed class McpEvalHarnessTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpeval_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

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
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.DisableAuth = true;
                        });
                    });
                });
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
        public async Task Harness_RunsSampleTasks_MatchesGoldenAssertionsAndBaseline()
        {
            var report = await new McpEvalHarness().RunAsync(WireInvoker(), Scenarios());

            // Golden assertions: every scripted task produced the expected results.
            report.AllPassed.Should().BeTrue(
                report.Scenarios.FirstOrDefault(s => !s.Passed)?.FailureReason);
            report.Totals.Errors.Should().Be(0);
            report.Totals.ErrorRate.Should().Be(0.0);
            report.Totals.Calls.Should().Be(report.Scenarios.Sum(s => s.Calls));

            // Baseline artifact: regenerate on intentional change, else pin as a regression reference.
            var baselinePath = BaselinePath();
            var actual = report.ToJson();
            if (Environment.GetEnvironmentVariable("BIFROST_MCP_EVAL_UPDATE") == "1" || !File.Exists(baselinePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
                await File.WriteAllTextAsync(baselinePath, actual);
            }
            else
            {
                var expected = await File.ReadAllTextAsync(baselinePath);
                Normalize(actual).Should().Be(Normalize(expected),
                    "the eval metrics must match the committed baseline; set BIFROST_MCP_EVAL_UPDATE=1 to regenerate after an intentional tool-output change");
            }
        }

        // ---- the scripted, no-LLM task suite ---------------------------------

        private static IReadOnlyList<McpEvalScenario> Scenarios() => new[]
        {
            new McpEvalScenario
            {
                Name = "schema-orientation",
                Steps = new[]
                {
                    new McpEvalStep
                    {
                        Tool = "bifrost_schema_overview",
                        Args = new Dictionary<string, object?> { ["detail"] = "summary" },
                        Assert = p => p.GetProperty("tableCount").GetInt32() == 2
                            ? null : "expected 2 tables in the overview",
                    },
                    new McpEvalStep
                    {
                        Tool = "bifrost_describe_table",
                        Args = new Dictionary<string, object?> { ["table"] = "orders" },
                        Assert = p => p.GetProperty("columns").EnumerateArray()
                            .Any(c => c.GetProperty("name").GetString() == "total")
                            ? null : "orders should expose a 'total' column",
                    },
                },
            },
            new McpEvalScenario
            {
                // Realistic multi-step task: "find customer 1's open orders and their total value."
                Name = "customer-open-orders-and-total",
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
                        Assert = p => p.GetProperty("returnedCount").GetInt32() == 2
                            ? null : "customer 1 has exactly 2 open orders",
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
                                new Dictionary<string, object?> { ["fn"] = "count" },
                            },
                            ["filter"] = new Dictionary<string, object?>
                            {
                                ["customer_id"] = new Dictionary<string, object?> { ["_eq"] = 1 },
                                ["status"] = new Dictionary<string, object?> { ["_eq"] = "open" },
                            },
                        },
                        Assert = p =>
                        {
                            var measures = p.GetProperty("groups").EnumerateArray().Single().GetProperty("measures");
                            return measures.GetProperty("sum_total").GetDouble() == 150.0
                                ? null : "customer 1's open-order total should be 150";
                        },
                    },
                },
            },
        };

        // ---- wire invoker: real MCP tools/call over an in-memory stream transport ----

        private McpToolInvoker WireInvoker() => async (tool, args) =>
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor, EndpointPath,
                userContextProvider: () => new Dictionary<string, object?>());

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-eval");
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

        /// <summary>Locates the committed baseline path next to this test project's source.</summary>
        private static string BaselinePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.Mcp.Test.csproj")))
                dir = dir.Parent;
            if (dir is null)
                throw new InvalidOperationException("Could not locate the BifrostQL.Mcp.Test project directory.");
            return Path.Combine(dir.FullName, "Eval", "mcp-eval-baseline.json");
        }

        private static string Normalize(string json) => json.Replace("\r\n", "\n").Trim();
    }
}
