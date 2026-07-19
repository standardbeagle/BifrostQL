using System.IO.Pipelines;
using System.Text;
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
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Declared WRITE tools (mcp-tools slice 8): a declarative document's
    /// <c>mutation</c> tools routed EXCLUSIVELY through
    /// <see cref="IMutationIntentExecutor"/>, OFF by default, destructive actions
    /// gated on explicit confirmation. Pins protocol-adapter-security invariant 7/8:
    /// the adapter builds no predicate (scope comes from the pipeline via identity), a
    /// disabled surface builds zero intent, and a fixed column literal can never widen
    /// past a security transformer.
    /// </summary>
    public sealed class McpDeclaredMutationToolTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpdeclmut_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private DeclarativeToolDocument _document = null!;

        // Declared write tools over the tenant-filtered soft-delete orders table:
        // - create_order INSERT declares a FOREIGN tenant_id literal ("B") that must be
        //   overridden by the tenant transformer (never widens scope).
        // - rename_order UPDATE / remove_order DELETE address a row by a positional PK
        //   param only — the adapter builds no WHERE, so scope comes from the pipeline.
        private const string Document = """
            {
              "version": 1,
              "tools": [
                {
                  "name": "create_order",
                  "description": "Create an order for the caller's tenant.",
                  "params": {
                    "id": { "type": "int", "description": "new order id" },
                    "name": { "type": "string", "description": "order name" }
                  },
                  "mutation": {
                    "table": "main.orders",
                    "action": "insert",
                    "values": { "id": "$id", "name": "$name", "tenant_id": "B" }
                  }
                },
                {
                  "name": "rename_order",
                  "description": "Rename an existing order addressed by its primary key.",
                  "params": {
                    "orderId": { "type": "id", "description": "order primary key" },
                    "name": { "type": "string", "description": "new name" }
                  },
                  "mutation": {
                    "table": "main.orders",
                    "action": "update",
                    "byId": "orderId",
                    "values": { "name": "$name" }
                  }
                },
                {
                  "name": "remove_order",
                  "description": "Delete an order addressed by its primary key.",
                  "params": { "orderId": { "type": "id", "description": "order primary key" } },
                  "mutation": { "table": "main.orders", "action": "delete", "byId": "orderId" }
                }
              ]
            }
            """;

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
                    name TEXT NOT NULL,
                    deleted_at TEXT NULL
                )
                """,
                "INSERT INTO orders(id, tenant_id, name, deleted_at) VALUES (1, 'A', 'a-first', NULL), (2, 'B', 'b-only', NULL)",
            })
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Document));
            _document = new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(stream, "declared-mutation")).Load();

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
                {
                    e.ConnectionString = _connString;
                    e.Provider = "sqlite";
                    e.Path = EndpointPath;
                    e.Metadata = new[] { "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }" };
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

        private async Task<object?> DbScalarAsync(string sql)
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            var value = await cmd.ExecuteScalarAsync();
            return value == DBNull.Value ? null : value;
        }

        private async Task WithClientAsync(
            bool enableWrites, IMutationIntentExecutor? mutationExecutor, Func<McpClient, Task> body)
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var mutation = mutationExecutor ?? _host.Services.GetRequiredService<IMutationIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor, EndpointPath,
                userContextProvider: () => new Dictionary<string, object?> { ["tenant_id"] = "A" },
                mutationExecutor: mutation,
                enableWrites: enableWrites,
                declarativeTools: _document);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-declmut-test");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try { await body(client); }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>A mutation executor that only records whether it was ever called.</summary>
        private sealed class RecordingMutationExecutor : IMutationIntentExecutor
        {
            public int Calls { get; private set; }
            public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new MutationIntentResult { Value = 0, AffectedRows = 0 });
            }
            public Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new MutationBatchIntentResult { TotalAffected = 0 });
            }
        }

        [Fact]
        public async Task WritesDisabled_DeclaredMutationTools_NotListed_AndRefusedWithZeroPipelineCalls()
        {
            var spy = new RecordingMutationExecutor();
            await WithClientAsync(enableWrites: false, spy, async client =>
            {
                var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
                tools.Should().NotContain(new[] { "create_order", "rename_order", "remove_order" },
                    "declared write tools are OFF by default and must not be advertised");

                var result = await client.CallToolAsync("create_order", new Dictionary<string, object?>
                {
                    ["id"] = 99, ["name"] = "ghost",
                });
                result.IsError.Should().BeTrue("a disabled write surface refuses the call");

                spy.Calls.Should().Be(0, "a disabled write surface builds zero intent — the pipeline is never touched");
                (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE id = 99")).Should().Be(0L);
            });
        }

        [Fact]
        public async Task WritesEnabled_DeclaredMutationTools_Listed_WithDestructiveHints()
        {
            await WithClientAsync(enableWrites: true, null, async client =>
            {
                var tools = (await client.ListToolsAsync()).ToList();
                tools.Select(t => t.Name).Should().Contain(new[] { "create_order", "rename_order", "remove_order" });

                tools.Single(t => t.Name == "create_order").ProtocolTool.Annotations!.DestructiveHint.Should().NotBe(true,
                    "insert is not destructive");
                tools.Single(t => t.Name == "rename_order").ProtocolTool.Annotations!.DestructiveHint.Should().BeTrue();
                tools.Single(t => t.Name == "remove_order").ProtocolTool.Annotations!.DestructiveHint.Should().BeTrue();
            });
        }

        [Fact]
        public async Task Insert_DeclaredMutation_PinsCallerTenant_OverForeignLiteral()
        {
            await WithClientAsync(enableWrites: true, null, async client =>
            {
                // The document declares tenant_id = "B"; the tenant transformer must still
                // pin the caller's tenant ("A") — a fixed literal cannot widen scope.
                var insert = await client.CallToolAsync("create_order", new Dictionary<string, object?>
                {
                    ["id"] = 10, ["name"] = "declared-row",
                });
                insert.IsError.Should().NotBeTrue(insert.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

                (await DbScalarAsync("SELECT tenant_id FROM orders WHERE id = 10")).Should().Be("A",
                    "the security transformer overrides the declared foreign tenant literal");
                (await DbScalarAsync("SELECT name FROM orders WHERE id = 10")).Should().Be("declared-row");
            });
        }

        [Fact]
        public async Task Update_DeclaredMutation_CrossTenantRow_AffectsZeroRows()
        {
            await WithClientAsync(enableWrites: true, null, async client =>
            {
                // Row 2 belongs to tenant B; the tenant-A caller's scope ANDs onto the
                // pipeline write. The adapter built no predicate, yet the write is a no-op.
                var update = await client.CallToolAsync("rename_order", new Dictionary<string, object?>
                {
                    ["orderId"] = 2, ["name"] = "hijacked", ["confirm"] = true,
                });
                update.StructuredContent!.Value.GetProperty("result").GetInt32().Should().Be(0,
                    "a scoped-away write reports zero affected rows, not phantom success");
                (await DbScalarAsync("SELECT name FROM orders WHERE id = 2")).Should().Be("b-only");
            });
        }

        [Fact]
        public async Task DestructiveDeclaredMutation_Unconfirmed_BuildsNoIntent()
        {
            await WithClientAsync(enableWrites: true, null, async client =>
            {
                // rename_order (update) is destructive. Without confirm=true no intent is built.
                var result = await client.CallToolAsync("rename_order", new Dictionary<string, object?>
                {
                    ["orderId"] = 1, ["name"] = "should-not-apply",
                });
                result.IsError.Should().BeTrue("a destructive tool requires explicit confirmation");
                result.Content.OfType<TextContentBlock>().First().Text.Should().Contain("confirm");

                (await DbScalarAsync("SELECT name FROM orders WHERE id = 1")).Should().Be("a-first",
                    "an unconfirmed destructive call must make no change");
            });
        }

        [Fact]
        public async Task Delete_DeclaredMutation_Confirmed_SoftDeletesThroughPipeline()
        {
            await WithClientAsync(enableWrites: true, null, async client =>
            {
                var delete = await client.CallToolAsync("remove_order", new Dictionary<string, object?>
                {
                    ["orderId"] = 1, ["confirm"] = true,
                });
                delete.IsError.Should().NotBeTrue(delete.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

                // Soft-delete table: the pipeline stamps deleted_at rather than removing —
                // the adapter never special-cased soft-delete.
                (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE id = 1")).Should().Be(1L);
                (await DbScalarAsync("SELECT deleted_at FROM orders WHERE id = 1")).Should().NotBeNull();
            });
        }
    }
}
