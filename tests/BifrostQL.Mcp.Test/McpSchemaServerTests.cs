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
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// End-to-end MCP integration tests: a real <see cref="McpServer"/> built by
    /// <see cref="BifrostMcpServerFactory"/> over an in-memory stream transport,
    /// talked to by the official SDK client — a full handshake plus tools/list,
    /// tools/call, resources/list, and resources/read over the wire. The fixture
    /// database is the same in-memory SQLite + AddBifrostEndpoints host the
    /// protocol-adapter conformance kit uses, so the model comes from the real
    /// cached-model path (<see cref="IQueryIntentExecutor.GetModelAsync"/>).
    /// </summary>
    public sealed class McpSchemaServerTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcp_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

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
            foreach (var sql in new[]
            {
                """
                CREATE TABLE customers (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL
                )
                """,
                """
                CREATE TABLE orders (
                    id INTEGER PRIMARY KEY,
                    customer_id INTEGER NOT NULL REFERENCES customers(id),
                    tenant_id TEXT NOT NULL,
                    name TEXT NOT NULL,
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

            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(executor);

            // In-memory duplex wire: client writes → server reads, server writes → client reads.
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                serverName: "BifrostQL-test");
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

        // ---- handshake -------------------------------------------------------

        [Fact]
        public void Handshake_ReportsBifrostServerWithToolsAndResources()
        {
            _client.ServerInfo.Name.Should().Be("BifrostQL");
            _client.ServerCapabilities.Tools.Should().NotBeNull();
            _client.ServerCapabilities.Resources.Should().NotBeNull();
            _client.ServerInstructions.Should().Contain("bifrost_schema_overview");
        }

        // ---- tools/list ------------------------------------------------------

        [Fact]
        public async Task ListTools_ExposesSchemaAndDataTools_WithOutputSchemasAndReadOnlyAnnotations()
        {
            var tools = await _client.ListToolsAsync();

            tools.Select(t => t.Name).Should().BeEquivalentTo(
                "bifrost_schema_overview", "bifrost_describe_table", "bifrost_query", "bifrost_row_context",
                "bifrost_aggregate", "bifrost_search");

            foreach (var tool in tools)
            {
                tool.ReturnJsonSchema.Should().NotBeNull($"tool {tool.Name} must declare an outputSchema");
                tool.ProtocolTool.Annotations.Should().NotBeNull();
                tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue($"tool {tool.Name} is read-only");
                tool.Description.Should().NotBeNullOrWhiteSpace();
            }
        }

        // ---- bifrost_schema_overview ----------------------------------------

        [Fact]
        public async Task SchemaOverview_Summary_ReturnsStructuredTableMapWithRelationshipsAndNotes()
        {
            var result = await _client.CallToolAsync("bifrost_schema_overview",
                new Dictionary<string, object?> { ["detail"] = "summary" });

            result.IsError.Should().NotBeTrue();
            result.StructuredContent.Should().NotBeNull();
            var payload = result.StructuredContent!.Value;

            payload.GetProperty("detail").GetString().Should().Be("summary");
            payload.GetProperty("tableCount").GetInt32().Should().Be(3);

            var tables = payload.GetProperty("tables").EnumerateArray()
                .ToDictionary(t => t.GetProperty("name").GetString()!);
            tables.Keys.Should().BeEquivalentTo("customers", "orders", "order_items");

            var orders = tables["orders"];
            orders.GetProperty("primaryKey").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("id");
            orders.GetProperty("references").EnumerateArray().Select(e => e.GetString())
                .Should().Contain("customer_id -> customers.id");
            orders.GetProperty("referencedBy").EnumerateArray().Select(e => e.GetString())
                .Should().Contain("order_items.order_id -> id");
            orders.GetProperty("notes").EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo("rows are tenant-scoped", "soft-deleted rows hidden");

            // Summary omits per-table column lists.
            orders.TryGetProperty("columns", out _).Should().BeFalse();
        }

        [Fact]
        public async Task SchemaOverview_Full_InlinesCondensedColumnLists()
        {
            var result = await _client.CallToolAsync("bifrost_schema_overview",
                new Dictionary<string, object?> { ["detail"] = "full" });

            result.IsError.Should().NotBeTrue();
            var payload = result.StructuredContent!.Value;
            payload.GetProperty("detail").GetString().Should().Be("full");

            var customers = payload.GetProperty("tables").EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "customers");
            var columns = customers.GetProperty("columns").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            columns.Should().Contain(c => c!.StartsWith("id: INTEGER"));
            columns.Should().Contain(c => c!.StartsWith("name: TEXT"));
        }

        // ---- bifrost_describe_table -----------------------------------------

        [Fact]
        public async Task DescribeTable_ReturnsColumnsKeysAndForeignKeysBothDirections()
        {
            var result = await _client.CallToolAsync("bifrost_describe_table",
                new Dictionary<string, object?> { ["table"] = "orders" });

            result.IsError.Should().NotBeTrue();
            result.StructuredContent.Should().NotBeNull();
            var payload = result.StructuredContent!.Value;

            payload.GetProperty("table").GetString().Should().Be("orders");
            payload.GetProperty("primaryKey").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("id");

            var columns = payload.GetProperty("columns").EnumerateArray()
                .ToDictionary(c => c.GetProperty("name").GetString()!);
            columns.Keys.Should().BeEquivalentTo("id", "customer_id", "tenant_id", "name", "deleted_at");
            columns["id"].GetProperty("primaryKey").GetBoolean().Should().BeTrue();
            columns["deleted_at"].GetProperty("nullable").GetBoolean().Should().BeTrue();
            columns["customer_id"].GetProperty("nullable").GetBoolean().Should().BeFalse();

            var fkOut = payload.GetProperty("foreignKeysOut").EnumerateArray().Single();
            fkOut.GetProperty("referencesTable").GetString().Should().Be("customers");
            fkOut.GetProperty("columns").EnumerateArray().Select(e => e.GetString()).Should().Equal("customer_id");
            fkOut.GetProperty("referencesColumns").EnumerateArray().Select(e => e.GetString()).Should().Equal("id");

            var fkIn = payload.GetProperty("foreignKeysIn").EnumerateArray().Single();
            fkIn.GetProperty("table").GetString().Should().Be("order_items");
            fkIn.GetProperty("columns").EnumerateArray().Select(e => e.GetString()).Should().Equal("order_id");
        }

        [Fact]
        public async Task DescribeTable_SurfacesBehaviorNotes_WithoutRawMetadataKeys()
        {
            var result = await _client.CallToolAsync("bifrost_describe_table",
                new Dictionary<string, object?> { ["table"] = "orders" });

            var notes = result.StructuredContent!.Value.GetProperty("behaviorNotes").EnumerateArray()
                .Select(e => e.GetString()!).ToArray();

            notes.Should().Contain(n => n.Contains("tenant") && n.Contains("tenant_id"));
            notes.Should().Contain(n => n.Contains("Soft-deleted") && n.Contains("deleted_at"));
            // Raw metadata keys are configuration surface, never client vocabulary.
            notes.Should().NotContain(n => n.Contains("tenant-filter") || n.Contains("soft-delete:"));
        }

        [Fact]
        public async Task DescribeTable_UnknownTable_ReturnsPromptStyleErrorWithSuggestionAndTableList()
        {
            var result = await _client.CallToolAsync("bifrost_describe_table",
                new Dictionary<string, object?> { ["table"] = "ordrs" });

            result.IsError.Should().BeTrue();
            var text = result.Content.OfType<TextContentBlock>().Single().Text;
            text.Should().Contain("Unknown table 'ordrs'");
            text.Should().Contain("Did you mean 'orders'?");
            text.Should().ContainAll("customers", "order_items", "orders");
        }

        // ---- resources -------------------------------------------------------

        [Fact]
        public async Task ListResources_ExposesOverviewAndOneResourcePerTable_WithStableUris()
        {
            var resources = await _client.ListResourcesAsync();

            resources.Select(r => r.Uri).Should().BeEquivalentTo(
                "bifrost://schema/overview",
                "bifrost://schema/customers",
                "bifrost://schema/orders",
                "bifrost://schema/order_items");
            resources.Should().OnlyContain(r => r.MimeType == "application/json");
        }

        [Fact]
        public async Task ReadResource_Overview_ReturnsFullDetailSchemaJson()
        {
            var result = await _client.ReadResourceAsync("bifrost://schema/overview");

            var contents = result.Contents.OfType<TextResourceContents>().Single();
            var payload = JsonDocument.Parse(contents.Text).RootElement;
            payload.GetProperty("detail").GetString().Should().Be("full");
            payload.GetProperty("tableCount").GetInt32().Should().Be(3);
        }

        [Fact]
        public async Task ReadResource_Table_ReturnsSameShapeAsDescribeTableTool()
        {
            var result = await _client.ReadResourceAsync("bifrost://schema/orders");

            var contents = result.Contents.OfType<TextResourceContents>().Single();
            var payload = JsonDocument.Parse(contents.Text).RootElement;
            payload.GetProperty("table").GetString().Should().Be("orders");
            payload.GetProperty("foreignKeysOut").EnumerateArray().Should().HaveCount(1);
        }

        [Fact]
        public async Task ReadResource_UnknownTable_FailsWithPromptStyleError()
        {
            var act = () => _client.ReadResourceAsync("bifrost://schema/ordrs").AsTask();

            var ex = await act.Should().ThrowAsync<McpException>();
            ex.Which.Message.Should().Contain("Did you mean 'orders'?");
        }
    }
}
