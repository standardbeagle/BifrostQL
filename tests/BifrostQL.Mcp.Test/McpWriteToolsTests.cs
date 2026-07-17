using System.IO.Pipelines;
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
    /// The MCP write surface (<c>bifrost_insert</c>/<c>bifrost_update</c>/<c>bifrost_delete</c>)
    /// over the real SDK client/server pair and the real mutation pipeline. The gate
    /// facts pin protocol-adapter-security invariant 7: the write surface is OFF by
    /// construction (never listed when disabled) and routes exclusively through
    /// <see cref="IMutationIntentExecutor"/> when enabled. Tenant-scoping / soft-delete
    /// semantics are proven exhaustively by the conformance derivation; here we prove
    /// the gate and the happy write round-trip.
    /// </summary>
    public sealed class McpWriteToolsTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpwrite_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;

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

        /// <summary>
        /// Drives one MCP session over an in-memory stream transport, built exactly as
        /// the adapter builds it (read + optional write surface), against the real
        /// pipeline with a tenant-A user context.
        /// </summary>
        private async Task WithClientAsync(bool enableWrites, Func<McpClient, Task> body)
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var mutation = _host.Services.GetRequiredService<IMutationIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(
                executor,
                userContextProvider: () => new Dictionary<string, object?> { ["tenant_id"] = "A" },
                mutationExecutor: mutation,
                enableWrites: enableWrites);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-write-test");
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

        [Fact]
        public async Task WritesDisabled_WriteToolsAreNeverListed_AndCannotBeCalled()
        {
            await WithClientAsync(enableWrites: false, async client =>
            {
                var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
                tools.Should().NotContain(new[] { "bifrost_insert", "bifrost_update", "bifrost_delete" },
                    "the write surface is OFF by construction and must not even be advertised");

                // Probing the disabled surface builds zero intent — it is an unknown tool.
                var act = () => client.CallToolAsync("bifrost_insert",
                    new Dictionary<string, object?> { ["table"] = "orders", ["values"] = new Dictionary<string, object?> { ["name"] = "x" } }).AsTask();
                await act.Should().ThrowAsync<Exception>();

                (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE name = 'x'")).Should().Be(0L);
            });
        }

        [Fact]
        public async Task WritesEnabled_WriteToolsAreListed()
        {
            await WithClientAsync(enableWrites: true, async client =>
            {
                var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
                tools.Should().Contain(new[] { "bifrost_insert", "bifrost_update", "bifrost_delete" });
            });
        }

        [Fact]
        public async Task Insert_PinsTenant_ThenUpdateAndDeleteRouteThroughThePipeline()
        {
            await WithClientAsync(enableWrites: true, async client =>
            {
                // Insert: the caller tries to plant the row in tenant B; the pipeline pins tenant A.
                var insert = await client.CallToolAsync("bifrost_insert", new Dictionary<string, object?>
                {
                    ["table"] = "orders",
                    ["values"] = new Dictionary<string, object?> { ["id"] = 10, ["name"] = "new-row", ["tenant_id"] = "B" },
                });
                insert.IsError.Should().NotBeTrue(insert.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                (await DbScalarAsync("SELECT tenant_id FROM orders WHERE id = 10")).Should().Be("A",
                    "the tenant transformer pins the caller's tenant over the client value");

                // Update within scope affects the row and reports the count.
                var update = await client.CallToolAsync("bifrost_update", new Dictionary<string, object?>
                {
                    ["table"] = "orders",
                    ["id"] = 10,
                    ["set"] = new Dictionary<string, object?> { ["name"] = "renamed" },
                });
                update.StructuredContent!.Value.GetProperty("result").GetInt32().Should().Be(1);
                (await DbScalarAsync("SELECT name FROM orders WHERE id = 10")).Should().Be("renamed");

                // Delete on a soft-delete table stamps deleted_at instead of removing.
                var delete = await client.CallToolAsync("bifrost_delete", new Dictionary<string, object?>
                {
                    ["table"] = "orders", ["id"] = 10,
                });
                delete.IsError.Should().NotBeTrue(delete.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                (await DbScalarAsync("SELECT COUNT(*) FROM orders WHERE id = 10")).Should().Be(1L, "soft delete keeps the row");
                (await DbScalarAsync("SELECT deleted_at FROM orders WHERE id = 10")).Should().NotBeNull();
            });
        }

        [Fact]
        public async Task Update_CrossTenantRow_AffectsZeroRows()
        {
            await WithClientAsync(enableWrites: true, async client =>
            {
                // Row 2 is tenant B; the tenant-A caller's scope ANDs onto the write → no-op.
                var update = await client.CallToolAsync("bifrost_update", new Dictionary<string, object?>
                {
                    ["table"] = "orders",
                    ["id"] = 2,
                    ["set"] = new Dictionary<string, object?> { ["name"] = "hijacked" },
                });
                update.StructuredContent!.Value.GetProperty("result").GetInt32().Should().Be(0,
                    "a scoped-away write must report zero affected rows, never phantom success");
                (await DbScalarAsync("SELECT name FROM orders WHERE id = 2")).Should().Be("b-only");
            });
        }
    }
}
