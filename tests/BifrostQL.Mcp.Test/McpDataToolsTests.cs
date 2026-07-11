using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// End-to-end tests for the row-reading MCP tools (<c>bifrost_query</c>,
    /// <c>bifrost_row_context</c>) over the real SDK client/server pair and the
    /// real intent-executor pipeline. The fixture registers the standard host
    /// (which includes the tenant and soft-delete filter transformers) and the
    /// MCP server runs with a tenant-A user context, so every assertion about
    /// visible rows doubles as an assertion that the security seam applies to
    /// protocol-adapter traffic.
    ///
    /// Fixture data: customers(2) → orders(4: two live tenant-A, one soft-deleted
    /// tenant-A, one live tenant-B) → order_items(32), plus a composite-PK
    /// shipments table.
    /// </summary>
    public sealed class McpDataToolsTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcpdata_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

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
                """
                CREATE TABLE shipments (
                    order_id INTEGER NOT NULL,
                    seq INTEGER NOT NULL,
                    note TEXT NOT NULL,
                    PRIMARY KEY (order_id, seq)
                )
                """,
                "INSERT INTO customers(id, name) VALUES (1, 'Acme Corp'), (2, 'Globex')",
                """
                INSERT INTO orders(id, customer_id, tenant_id, name, deleted_at) VALUES
                    (1, 1, 'A', 'order-a1', NULL),
                    (2, 1, 'A', 'order-a2', '2026-01-01'),
                    (3, 1, 'B', 'order-b1', NULL),
                    (4, 2, 'A', 'order-a3', NULL)
                """,
                "INSERT INTO shipments(order_id, seq, note) VALUES (4, 1, 'first leg'), (4, 2, 'second leg')",
            };
            // 30 items on order 1 (forces query paging past the default 25) and
            // 2 on order 4.
            for (var i = 1; i <= 30; i++)
                statements.Add($"INSERT INTO order_items(id, order_id, sku) VALUES ({i}, 1, 'SKU-{i:000}')");
            statements.Add("INSERT INTO order_items(id, order_id, sku) VALUES (31, 4, 'SKU-031'), (32, 4, 'SKU-032')");
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
                serverName: "BifrostQL-data-test");
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

        private static string[] RowValues(JsonElement payload, string column) =>
            payload.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty(column).ToString())
                .ToArray();

        // ---- bifrost_query: paging + cursor -----------------------------------

        [Fact]
        public async Task Query_DefaultsTo25Rows_WithTruncationSteeringAndNextCursor()
        {
            var payload = await CallOk("bifrost_query", new() { ["table"] = "order_items" });

            payload.GetProperty("totalCount").GetInt32().Should().Be(32);
            payload.GetProperty("returnedCount").GetInt32().Should().Be(25);
            payload.GetProperty("rows").GetArrayLength().Should().Be(25);
            payload.GetProperty("offset").GetInt32().Should().Be(0);
            payload.GetProperty("nextCursor").GetString().Should().NotBeNullOrWhiteSpace();
            payload.GetProperty("message").GetString().Should()
                .Contain("32 rows match; showing 25").And.Contain("filter");
        }

        [Fact]
        public async Task Query_CursorRoundTrip_ContinuesWhereThePreviousPageEnded()
        {
            var first = await CallOk("bifrost_query", new() { ["table"] = "order_items" });
            var cursor = first.GetProperty("nextCursor").GetString()!;

            // The cursor alone is a complete continuation — no other arguments.
            var second = await CallOk("bifrost_query", new()
            {
                ["page"] = new Dictionary<string, object?> { ["cursor"] = cursor },
            });

            second.GetProperty("offset").GetInt32().Should().Be(25);
            second.GetProperty("returnedCount").GetInt32().Should().Be(7);
            // Default sort is primary key ascending, so page 2 is exactly ids 26..32.
            second.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("id").GetInt64())
                .Should().Equal(26, 27, 28, 29, 30, 31, 32);
            second.TryGetProperty("nextCursor", out _).Should().BeFalse("all rows are exhausted");
        }

        [Fact]
        public async Task Query_CursorForOtherTable_FailsFastInsteadOfSilentlySwitching()
        {
            var first = await CallOk("bifrost_query", new() { ["table"] = "order_items" });
            var cursor = first.GetProperty("nextCursor").GetString()!;

            var text = await CallError("bifrost_query", new()
            {
                ["table"] = "customers",
                ["page"] = new Dictionary<string, object?> { ["cursor"] = cursor },
            });

            text.Should().Contain("order_items").And.Contain("customers");
        }

        [Fact]
        public async Task Query_GarbageCursor_PromptsToUseNextCursor()
        {
            var text = await CallError("bifrost_query", new()
            {
                ["page"] = new Dictionary<string, object?> { ["cursor"] = "not-a-cursor" },
            });

            text.Should().Contain("Invalid cursor").And.Contain("nextCursor");
        }

        /// <summary>
        /// Decodes a server-issued cursor, overwrites one field, and re-encodes —
        /// the exact shape of a crafted-cursor attack (the token is unsigned
        /// base64 JSON, so any caller can do this).
        /// </summary>
        private static string TamperCursor(string cursor, string property, JsonNode? value)
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))!.AsObject();
            json[property] = value;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));
        }

        [Theory]
        [InlineData("Limit", -1)] // would hit the dialect's no-limit sentinel → full-table dump
        [InlineData("Limit", 0)]
        [InlineData("Limit", 10_000)] // above MaxPageLimit (200)
        [InlineData("Offset", -5)]
        public async Task Query_CursorWithOutOfRangePagingValues_RejectedAsInvalidCursorNotClamped(
            string property, int value)
        {
            var first = await CallOk("bifrost_query", new() { ["table"] = "order_items" });
            var cursor = first.GetProperty("nextCursor").GetString()!;

            var text = await CallError("bifrost_query", new()
            {
                ["page"] = new Dictionary<string, object?> { ["cursor"] = TamperCursor(cursor, property, value) },
            });

            // Same prompt as an undecodable cursor: server-issued cursors never
            // carry these values, so out-of-range fields mean tampering, and no
            // rows may be returned (a limit of -1 would otherwise dump the table).
            text.Should().Contain("Invalid cursor").And.Contain("nextCursor");
        }

        [Fact]
        public async Task Query_CursorWithUnknownDetail_RejectedAsInvalidCursor()
        {
            var first = await CallOk("bifrost_query", new() { ["table"] = "order_items" });
            var cursor = first.GetProperty("nextCursor").GetString()!;

            var text = await CallError("bifrost_query", new()
            {
                ["page"] = new Dictionary<string, object?> { ["cursor"] = TamperCursor(cursor, "Detail", "bogus") },
            });

            text.Should().Contain("Invalid cursor").And.Contain("nextCursor");
        }

        /// <summary>
        /// Every field a cursor carries is caller-controlled, so each one is
        /// tampered in turn with values a server-issued cursor can never
        /// contain. All of them must collapse to the shared invalid-cursor
        /// prompt as a transport-level tool error (IsError, zero rows) —
        /// never an unhandled exception (which would surface as a protocol
        /// fault) and never a fresh-argument-style message (which would
        /// reveal that the tampered value reached deeper validation).
        /// </summary>
        [Theory]
        // FilterJson: garbage that fails JSON parsing entirely.
        [InlineData("FilterJson", "\"{{{not json\"")]
        // FilterJson: valid JSON, wrong shape (array instead of filter object).
        [InlineData("FilterJson", "\"[1,2,3]\"")]
        // FilterJson: valid filter shape but a column the table does not have.
        [InlineData("FilterJson", "\"{\\\"no_such_column\\\":{\\\"_eq\\\":1}}\"")]
        // FilterJson: wrong JSON type for the property itself.
        [InlineData("FilterJson", "42")]
        // Fields: empty array (fresh path rejects this; resume must too).
        [InlineData("Fields", "[]")]
        // Fields: null entry — would NRE in column resolution if unchecked.
        [InlineData("Fields", "[\"name\", null]")]
        // Fields: column the table does not have.
        [InlineData("Fields", "[\"no_such_column\"]")]
        // Sort: unknown column and malformed token (no _asc/_desc suffix).
        [InlineData("Sort", "[\"no_such_column_asc\"]")]
        [InlineData("Sort", "[\"id\"]")]
        [InlineData("Sort", "[null]")]
        // Table: unknown table, and a casing variant the server never issues.
        [InlineData("Table", "\"order_itemz\"")]
        [InlineData("Table", "\"ORDER_ITEMS\"")]
        // Detail / Version: nulled-out or unsupported snapshot metadata.
        [InlineData("Detail", "null")]
        [InlineData("Version", "2")]
        public async Task Query_TamperedCursorField_RejectedAsInvalidCursorPrompt(
            string property, string rawJsonValue)
        {
            var first = await CallOk("bifrost_query", new() { ["table"] = "order_items" });
            var cursor = first.GetProperty("nextCursor").GetString()!;

            var result = await _client.CallToolAsync("bifrost_query", new Dictionary<string, object?>
            {
                ["page"] = new Dictionary<string, object?>
                {
                    ["cursor"] = TamperCursor(cursor, property, JsonNode.Parse(rawJsonValue)),
                },
            });

            // Transport-level tool error, not a protocol fault: the call
            // completes, IsError is set, no structured rows escape.
            result.IsError.Should().BeTrue("a tampered cursor must be rejected as a tool error");
            result.StructuredContent.Should().BeNull("no rows may be returned for a tampered cursor");
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Invalid cursor").And.Contain("nextCursor");
        }

        // ---- bifrost_query: security seam --------------------------------------

        [Fact]
        public async Task Query_OnTenantFilteredTable_HidesOtherTenantsAndSoftDeletedRows()
        {
            var payload = await CallOk("bifrost_query", new() { ["table"] = "orders", ["detail"] = "full" });

            // 4 rows exist; tenant B's row and the soft-deleted tenant-A row are
            // invisible through the intent seam without any caller-supplied filter.
            payload.GetProperty("totalCount").GetInt32().Should().Be(2);
            RowValues(payload, "name").Should().BeEquivalentTo("order-a1", "order-a3");
        }

        [Fact]
        public async Task Query_InjectionShapedFilterValue_BindsAsParameterAndMatchesNothing()
        {
            var payload = await CallOk("bifrost_query", new()
            {
                ["table"] = "orders",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?> { ["_eq"] = "x' OR '1'='1" },
                },
            });

            payload.GetProperty("totalCount").GetInt32().Should().Be(0);
            payload.GetProperty("rows").GetArrayLength().Should().Be(0);
        }

        // ---- bifrost_query: filter operators ------------------------------------

        [Fact]
        public async Task Query_FilterOperators_CompileToTheExistingOperatorSet()
        {
            var inPayload = await CallOk("bifrost_query", new()
            {
                ["table"] = "order_items",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["id"] = new Dictionary<string, object?> { ["_in"] = new object[] { 1, 2, 31 } },
                },
            });
            inPayload.GetProperty("totalCount").GetInt32().Should().Be(3);

            var betweenPayload = await CallOk("bifrost_query", new()
            {
                ["table"] = "order_items",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["id"] = new Dictionary<string, object?> { ["_between"] = new object[] { 1, 3 } },
                },
            });
            betweenPayload.GetProperty("totalCount").GetInt32().Should().Be(3);

            var containsPayload = await CallOk("bifrost_query", new()
            {
                ["table"] = "order_items",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["sku"] = new Dictionary<string, object?> { ["_contains"] = "-003" },
                },
            });
            containsPayload.GetProperty("totalCount").GetInt32().Should().Be(1, "only SKU-003 contains '-003'");
        }

        [Fact]
        public async Task Query_SiblingColumnsAndTogether_AndOrGroupsMapNatively()
        {
            // Sibling keys form an implicit AND: order 4's items only.
            var andPayload = await CallOk("bifrost_query", new()
            {
                ["table"] = "order_items",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["order_id"] = new Dictionary<string, object?> { ["_eq"] = 4 },
                    ["sku"] = new Dictionary<string, object?> { ["_contains"] = "032" },
                },
            });
            andPayload.GetProperty("totalCount").GetInt32().Should().Be(1);

            // Explicit OR maps to the native Or branch of TableFilter.
            var orPayload = await CallOk("bifrost_query", new()
            {
                ["table"] = "order_items",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["or"] = new object[]
                    {
                        new Dictionary<string, object?> { ["id"] = new Dictionary<string, object?> { ["_eq"] = 1 } },
                        new Dictionary<string, object?> { ["id"] = new Dictionary<string, object?> { ["_eq"] = 32 } },
                    },
                },
            });
            orPayload.GetProperty("totalCount").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task Query_NullOperator_FiltersOnNullness()
        {
            // deleted_at IS NULL — combined with the automatic soft-delete filter
            // this is a no-op, so both live tenant-A orders return.
            var payload = await CallOk("bifrost_query", new()
            {
                ["table"] = "orders",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["deleted_at"] = new Dictionary<string, object?> { ["_null"] = true },
                },
            });
            payload.GetProperty("totalCount").GetInt32().Should().Be(2);
        }

        // ---- bifrost_query: detail / fields -------------------------------------

        [Fact]
        public async Task Query_Detail_ChangesTheProjectedColumnSet()
        {
            var summary = await CallOk("bifrost_query", new() { ["table"] = "orders" });
            var summaryRow = summary.GetProperty("rows").EnumerateArray().First();
            // summary = primary key + display column ('name'); TEXT columns carry
            // no declared length, so no short-string columns qualify.
            summaryRow.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name");

            var full = await CallOk("bifrost_query", new() { ["table"] = "orders", ["detail"] = "full" });
            var fullRow = full.GetProperty("rows").EnumerateArray().First();
            fullRow.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                "id", "customer_id", "tenant_id", "name", "deleted_at");
        }

        [Fact]
        public async Task Query_ExplicitFields_OverrideDetail()
        {
            var payload = await CallOk("bifrost_query", new()
            {
                ["table"] = "orders",
                ["fields"] = new[] { "name", "customer_id" },
            });

            var row = payload.GetProperty("rows").EnumerateArray().First();
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("name", "customer_id");
        }

        // ---- bifrost_query: errors as prompts ------------------------------------

        [Fact]
        public async Task Query_UnknownTable_PromptsWithSuggestionAndTableList()
        {
            var text = await CallError("bifrost_query", new() { ["table"] = "ordrs" });

            text.Should().Contain("Unknown table 'ordrs'")
                .And.Contain("Did you mean 'orders'?")
                .And.Contain("customers");
        }

        [Fact]
        public async Task Query_UnknownFilterColumn_PromptsWithDidYouMeanAndColumnList()
        {
            var text = await CallError("bifrost_query", new()
            {
                ["table"] = "orders",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["nmae"] = new Dictionary<string, object?> { ["_eq"] = "x" },
                },
            });

            text.Should().Contain("Unknown column 'nmae'")
                .And.Contain("Did you mean 'name'?")
                .And.Contain("customer_id");
        }

        [Fact]
        public async Task Query_UnknownOperator_PromptsWithOperatorListAndInlineExample()
        {
            var text = await CallError("bifrost_query", new()
            {
                ["table"] = "orders",
                ["filter"] = new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?> { ["_equals"] = "x" },
                },
            });

            text.Should().Contain("Unknown filter operator '_equals'")
                .And.Contain("_eq").And.Contain("_between").And.Contain("_null")
                .And.Contain("Example:");
        }

        // ---- bifrost_row_context ---------------------------------------------

        [Fact]
        public async Task RowContext_ReturnsRowResolvedParentsAndChildSummaries()
        {
            var payload = await CallOk("bifrost_row_context", new() { ["table"] = "orders", ["id"] = 1 });

            payload.GetProperty("table").GetString().Should().Be("orders");
            payload.GetProperty("id").EnumerateArray().Single().GetInt64().Should().Be(1);
            payload.GetProperty("row").GetProperty("name").GetString().Should().Be("order-a1");

            // Parent: the FK to customers resolves to its key AND display name.
            var parent = payload.GetProperty("parents").EnumerateArray()
                .Single(p => p.GetProperty("table").GetString() == "customers");
            parent.GetProperty("found").GetBoolean().Should().BeTrue();
            parent.GetProperty("id").EnumerateArray().Single().GetInt64().Should().Be(1);
            parent.GetProperty("displayName").GetString().Should().Be("Acme Corp");

            // Child summary: count + first rows (top 5 of 30), in one call.
            var items = payload.GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("table").GetString() == "order_items");
            items.GetProperty("totalCount").GetInt32().Should().Be(30);
            items.GetProperty("rows").GetArrayLength().Should().Be(5);
            items.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("id").GetInt64())
                .Should().Equal(1, 2, 3, 4, 5);
        }

        [Fact]
        public async Task RowContext_RowOutsideTenantScope_ReadsAsNotFound()
        {
            // Order 3 exists but belongs to tenant B; the tenant-A session must
            // get a not-found prompt, not the row.
            var text = await CallError("bifrost_row_context", new() { ["table"] = "orders", ["id"] = 3 });

            text.Should().Contain("No row found in 'orders'").And.Contain("access scope");
        }

        [Fact]
        public async Task RowContext_ChildSummaries_AreTenantAndSoftDeleteScoped()
        {
            var payload = await CallOk("bifrost_row_context", new() { ["table"] = "customers", ["id"] = 1 });

            // Customer 1 has 3 orders in the database: live tenant-A, soft-deleted
            // tenant-A, live tenant-B. Only the first may surface.
            var orders = payload.GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("table").GetString() == "orders");
            orders.GetProperty("totalCount").GetInt32().Should().Be(1);
            orders.GetProperty("rows").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .Should().Equal("order-a1");
        }

        [Fact]
        public async Task RowContext_CompositePrimaryKey_AcceptsArrayAndDelimitedForms()
        {
            var byArray = await CallOk("bifrost_row_context", new()
            {
                ["table"] = "shipments",
                ["id"] = new object[] { 4, 2 },
            });
            byArray.GetProperty("row").GetProperty("note").GetString().Should().Be("second leg");
            byArray.GetProperty("id").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(4, 2);

            var byDelimited = await CallOk("bifrost_row_context", new()
            {
                ["table"] = "shipments",
                ["id"] = "4|2",
            });
            byDelimited.GetProperty("row").GetProperty("note").GetString().Should().Be("second leg");
        }

        [Fact]
        public async Task RowContext_CompositeKeyArityMismatch_PromptsWithKeyColumnsAndForms()
        {
            var text = await CallError("bifrost_row_context", new() { ["table"] = "shipments", ["id"] = 4 });

            text.Should().Contain("primary key of 2 column(s)")
                .And.Contain("order_id").And.Contain("seq")
                .And.Contain("array");
        }
    }
}
