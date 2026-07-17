using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // ---- adapter identity seam (slice A) ----------------------------------

        /// <summary>
        /// Runs bifrost_query on the tenant-filtered orders table against a server
        /// whose user context comes from <paramref name="provider"/> — the exact seam
        /// <see cref="BifrostMcpAdapter"/> wires from <see cref="IBifrostAuthContextFactory"/>.
        /// </summary>
        private async Task<CallToolResult> QueryOrdersWith(Func<IDictionary<string, object?>> provider)
        {
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var options = BifrostMcpServerFactory.CreateServerOptions(executor, userContextProvider: provider);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                serverName: "BifrostQL-adapter-seam-test");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()));
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

        [Fact]
        public async Task AdapterProvider_StdioSessionNoPrincipal_YieldsEmptyContext_AndTenantReadFailsClosed()
        {
            // The adapter derives its provider from the shared auth factory. A stdio
            // session has no authenticated principal, so the factory projects an empty
            // (fail-closed) context — the adapter parses no claims of its own.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services);

            provider().Should().BeEmpty(
                "a stdio session carries no principal, so the shared factory projects an empty default context");

            var result = await QueryOrdersWith(provider);

            // Fail closed: the tenant-filtered table refuses the read — never an
            // empty/unfiltered success that would leak every tenant's rows.
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        [Fact]
        public async Task AdapterProvider_FactoryProjectsTenant_ScopesReadToThatTenant()
        {
            // When the shared factory projects a tenant identity, the adapter passes it
            // through unchanged to every intent — no bespoke re-derivation of scope.
            var factory = new StubTenantAuthFactory("A");
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services);

            var ctx = provider();
            ctx.Should().ContainKey("tenant_id");
            ctx["tenant_id"].Should().Be("A");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().NotBeTrue(
                result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

            var payload = result.StructuredContent!.Value;
            payload.GetProperty("totalCount").GetInt32().Should().Be(2);
            payload.GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Should().BeEquivalentTo("order-a1", "order-a3");
        }

        /// <summary>
        /// Stand-in <see cref="IBifrostAuthContextFactory"/> that projects a fixed tenant,
        /// standing for a real authenticated principal a later slice attaches to the carrier.
        /// Proves the adapter sources identity from the factory and forwards its projection
        /// unchanged.
        /// </summary>
        private sealed class StubTenantAuthFactory : IBifrostAuthContextFactory
        {
            private readonly string _tenantId;
            public StubTenantAuthFactory(string tenantId) => _tenantId = tenantId;

            public IDictionary<string, object?> CreateUserContext(HttpContext context)
                => new Dictionary<string, object?> { ["tenant_id"] = _tenantId };

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }

        // ---- per-transport credential source (slice C) ------------------------

        private const string TenantAToken = "tenant-a-token";

        /// <summary>
        /// A bearer options set whose only transport-specific part is
        /// <paramref name="credentialSource"/>: the SAME validator (token → tenant-A principal)
        /// and the SAME <see cref="IBifrostAuthContextFactory"/> projection back it, so stdio and
        /// HTTP differ only in WHERE the credential is read.
        /// </summary>
        private static McpAuthOptions BearerOptionsWith(Func<string?> credentialSource)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-a"),
                    new Claim("bifrost:tenant", "A"),
                },
                authenticationType: "Bearer"));

            return new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = credentialSource,
                ValidateBearerToken = token => token == TenantAToken ? principal : null,
            };
        }

        [Fact]
        public async Task CredentialSource_StdioEnv_SuppliesCredential_ScopesReadToTenant()
        {
            // Criterion 2: a stdio session reads its credential from the process environment
            // (no per-request principal on the wire) and the shared projection scopes the read.
            var envVar = $"BIFROST_MCP_TEST_TOKEN_{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable(envVar, TenantAToken);
            try
            {
                var options = BearerOptionsWith(McpCredentialSources.FromEnvironment(envVar));
                var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
                var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

                provider().Should().ContainKey("tenant_id").WhoseValue.Should().Be("A");

                var result = await QueryOrdersWith(provider);
                result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
                    .Select(row => row.GetProperty("name").GetString())
                    .Should().BeEquivalentTo("order-a1", "order-a3");
            }
            finally
            {
                Environment.SetEnvironmentVariable(envVar, null);
            }
        }

        [Fact]
        public async Task CredentialSource_HttpBearerHeader_SuppliesCredential_ScopesReadToTenant()
        {
            // Criterion 3: an HTTP-shaped session reads its credential from an
            // Authorization: Bearer header — using the seam only, NOT slice-5 transport wiring —
            // and the SAME projection yields the SAME tenant-scoped result as the stdio path.
            var options = BearerOptionsWith(
                McpCredentialSources.FromAuthorizationHeader(() => $"Bearer {TenantAToken}"));
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

            provider().Should().ContainKey("tenant_id").WhoseValue.Should().Be("A");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Should().BeEquivalentTo("order-a1", "order-a3");
        }

        [Fact]
        public void CredentialSource_StdioAndHttp_HitIdenticalProjection()
        {
            // Criterion 1: the projection call site is IDENTICAL for both transports — swapping the
            // env credential-read for the header credential-read produces the same projected
            // context, because only the credential-read step differs.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();

            var envVar = $"BIFROST_MCP_TEST_TOKEN_{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable(envVar, TenantAToken);
            try
            {
                var stdio = BifrostMcpAdapter.CreateUserContextProvider(
                    factory, _host.Services, BearerOptionsWith(McpCredentialSources.FromEnvironment(envVar)))();
                var http = BifrostMcpAdapter.CreateUserContextProvider(
                    factory, _host.Services,
                    BearerOptionsWith(McpCredentialSources.FromAuthorizationHeader(() => $"Bearer {TenantAToken}")))();

                // Same projected identity keys and the same tenant scope from either transport —
                // the credential-read step is the only difference (deep-comparing the principal
                // object itself is meaningless: it carries cyclic Claims references).
                http.Keys.Should().BeEquivalentTo(stdio.Keys,
                    "identity projection is transport-agnostic — only the credential-read step differs");
                http["tenant_id"].Should().Be(stdio["tenant_id"]).And.Be("A");
            }
            finally
            {
                Environment.SetEnvironmentVariable(envVar, null);
            }
        }

        [Fact]
        public async Task CredentialSource_AbsentOnEitherTransport_MintsNoIdentity_FailsClosed()
        {
            // Criterion 5: an absent credential on EITHER transport (unset env var / missing
            // header) mints no identity, so the empty context drives the fail-closed rejection —
            // never anonymous.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();

            var absentEnv = BearerOptionsWith(
                McpCredentialSources.FromEnvironment($"BIFROST_MCP_ABSENT_{Guid.NewGuid():N}"));
            var absentHeader = BearerOptionsWith(
                McpCredentialSources.FromAuthorizationHeader(() => null));

            foreach (var options in new[] { absentEnv, absentHeader })
            {
                var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);
                provider().Should().BeEmpty("an absent credential mints no identity — never anonymous");

                var result = await QueryOrdersWith(provider);
                result.IsError.Should().BeTrue();
                result.Content.OfType<TextContentBlock>().Single().Text
                    .Should().Contain("Tenant context required");
            }
        }

        [Theory]
        [InlineData("Bearer abc123", "abc123")]
        [InlineData("bearer abc123", "abc123")]      // scheme is case-insensitive
        [InlineData("Bearer   spaced  ", "spaced")]  // surrounding whitespace trimmed
        [InlineData("abc123", null)]                 // no scheme → no credential
        [InlineData("Basic abc123", null)]           // wrong scheme → no credential
        [InlineData("Bearer ", null)]                // empty token → no credential
        [InlineData("", null)]
        [InlineData(null, null)]
        public void ExtractBearerToken_ParsesOnlyAWellFormedBearerHeader(string? header, string? expected)
        {
            // The HTTP credential-read step is a pure parse of the Authorization header value;
            // anything that is not a well-formed Bearer header presents no credential.
            McpCredentialSources.ExtractBearerToken(header).Should().Be(expected);
        }

        [Fact]
        public void McpSource_AddsNoHttpTransportHosting()
        {
            // Criterion 4: this slice defines ONLY the credential-extraction seam; the HTTP
            // transport hosting (MapMcp/Kestrel/routing) is slice 5's job. A source scan confirms
            // src/BifrostQL.Mcp mounts no HTTP endpoint.
            var mcpSrcDir = FindMcpSourceDirectory();
            var files = Directory.EnumerateFiles(mcpSrcDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();
            files.Should().NotBeEmpty("the BifrostQL.Mcp source must be locatable or this guard is vacuous");

            string[] httpHostingTokens =
            {
                "MapMcp", "WithHttpTransport", "StreamableHttp", "UseRouting", "UseEndpoints",
                "MapPost", "MapGet", "ConfigureKestrel", "AddRouting", "ListenLocalhost",
            };

            var offenders = new List<string>();
            foreach (var file in files)
                foreach (var line in File.ReadLines(file))
                    foreach (var token in httpHostingTokens)
                        if (line.Contains(token, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)}: '{token}' in: {line.Trim()}");

            offenders.Should().BeEmpty(
                "slice C defines only the credential-extraction seam — HTTP transport hosting is slice 5");
        }

        // ---- configurable auth modes (slice B) --------------------------------

        [Fact]
        public async Task DefaultOptions_AnonymousNotGranted_TenantReadFailsClosed()
        {
            // Criterion 1: the dangerous opt-ins default OFF, so default options mint no
            // anonymous identity and a tenant-filtered read fails closed.
            var options = new McpAuthOptions();
            options.Mode.Should().Be(McpAuthMode.FailClosed, "the dangerous anonymous/bearer surfaces default OFF");

            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

            provider().Should().BeEmpty("no identity source is configured, so no anonymous identity is minted");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        [Fact]
        public void AnonymousDevMode_LogsDeliberateOptInWarningAtStartup()
        {
            // Criterion 2: enabling the anonymous/dev opt-in logs a startup warning mirroring
            // RespWireAdapter's EnableWrites posture warning.
            var captured = new CapturingLoggerFactory();
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var adapter = new BifrostMcpAdapter(executor, factory, _host.Services, captured,
                new McpAuthOptions { Mode = McpAuthMode.AnonymousDev });

            adapter.ConfigureAuth();

            captured.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
                .Which.Message.Should().Contain("ANONYMOUS/DEV").And.Contain("deliberate opt-in");
        }

        [Fact]
        public void DefaultFailClosedMode_LogsNoStartupWarning()
        {
            // The safe default is silent — only the deliberate opt-in warns.
            var captured = new CapturingLoggerFactory();
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var adapter = new BifrostMcpAdapter(executor, factory, _host.Services, captured, new McpAuthOptions());

            adapter.ConfigureAuth();

            captured.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        }

        [Fact]
        public async Task BearerMode_ValidToken_ProjectsPrincipalThroughFactory_ScopesToTenant()
        {
            // Criterion 3: a valid bearer token is validated BEFORE identity is minted; its
            // principal reaches the pipeline ONLY through the shared factory's projection.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-a"),
                    new Claim("bifrost:tenant", "A"),
                },
                authenticationType: "Bearer"));

            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                BearerToken = "valid-token",
                ValidateBearerToken = token => token == "valid-token" ? principal : null,
            };

            // Real shared factory (not a stub): proves the ClaimsPrincipal is projected only by
            // IBifrostAuthContextFactory, with no bespoke claim reading in the adapter.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

            var ctx = provider();
            ctx.Should().ContainKey("tenant_id");
            ctx["tenant_id"].Should().Be("A");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

            var payload = result.StructuredContent!.Value;
            payload.GetProperty("totalCount").GetInt32().Should().Be(2);
            payload.GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Should().BeEquivalentTo("order-a1", "order-a3");
        }

        [Theory]
        [InlineData("wrong-token")] // presented but invalid
        [InlineData(null)]          // absent
        public async Task BearerMode_InvalidOrAbsentToken_MintsNoIdentity_FailsClosed(string? presentedToken)
        {
            // Criterion 4: on a bearer (non-dev) server an absent or invalid token mints NO
            // identity, so the empty context drives the existing fail-closed rejection — never
            // a degraded/empty-but-permitted read.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-a"),
                    new Claim("bifrost:tenant", "A"),
                },
                authenticationType: "Bearer"));

            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                BearerToken = presentedToken,
                ValidateBearerToken = token => token == "valid-token" ? principal : null,
            };

            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

            provider().Should().BeEmpty(
                "an absent or invalid bearer token mints no identity — never a degraded pass-through");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        // ---- OIDC / token-exchange credential store (slice D) -----------------

        /// <summary>
        /// A stand-in <see cref="IMcpCredentialStore"/> that plays the role of a real IdP
        /// token-exchange endpoint WITHOUT calling one: it hands back a fixed candidate principal
        /// (or <c>null</c> for a failed/unknown exchange), records how many times it was invoked,
        /// and captures the upstream token it was asked to exchange. The counter proves the store
        /// is consulted on the per-call seam (not captured once); the null case proves the store
        /// never synthesizes an ambient identity to stand in for a failure.
        /// </summary>
        private sealed class FakeExchangeStore : IMcpCredentialStore
        {
            private readonly ClaimsPrincipal? _result;
            public FakeExchangeStore(ClaimsPrincipal? result) => _result = result;
            public int Invocations { get; private set; }
            public string? LastUpstreamToken { get; private set; }

            public Task<ClaimsPrincipal?> ExchangeAsync(string upstreamToken, CancellationToken cancellationToken)
            {
                Invocations++;
                LastUpstreamToken = upstreamToken;
                return Task.FromResult(_result);
            }
        }

        private const string UpstreamIdpToken = "upstream-idp-token";

        private static ClaimsPrincipal TenantAPrincipal() => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-a"),
                new Claim("bifrost:tenant", "A"),
            },
            authenticationType: "TokenExchange"));

        [Fact]
        public async Task TokenExchange_OptIn_NoStoreConfigured_DoesNotExchange_FailsClosed()
        {
            // Criterion 1: token exchange is opt-in. A credential is present on the wire (the
            // upstream IdP token is extracted by the slice-C source), but NO store is configured,
            // so no exchange is attempted, no identity is minted, and the tenant-filtered read
            // fails closed exactly like slice A/B — never a degraded pass-through.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = () => UpstreamIdpToken,
                // CredentialStore deliberately unset; ValidateBearerToken unset.
            };

            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);
            provider().Should().BeEmpty("no store is configured, so the upstream token is never exchanged");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        [Fact]
        public async Task TokenExchange_StoreReturnsNull_MintsNoIdentity_FailsClosed()
        {
            // Criterion 2: a store that returns null for an unknown/failed exchange mints NO
            // identity — the empty context drives the fail-closed rejection, never an
            // empty-but-permitted read. The store IS consulted (it tried the exchange and refused);
            // it did not silently synthesize an ambient identity.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var store = new FakeExchangeStore(result: null);
            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = () => UpstreamIdpToken,
                CredentialStore = store,
            };

            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);
            provider().Should().BeEmpty("a null exchange result mints no identity — never anonymous");

            store.Invocations.Should().BeGreaterThan(0, "the store was consulted, then refused — not skipped");
            store.LastUpstreamToken.Should().Be(UpstreamIdpToken,
                "the store exchanges the slice-C extracted upstream credential");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        [Fact]
        public async Task TokenExchange_SuccessfulExchange_ProjectsCandidateThroughFactory_ScopesToTenant()
        {
            // Criterion 3: a successful exchange returns a CANDIDATE principal that is projected
            // through the REAL shared IBifrostAuthContextFactory (same call site as slices A-C),
            // and the exchanged identity returns the caller's tenant-scoped rows.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var store = new FakeExchangeStore(TenantAPrincipal());
            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = () => UpstreamIdpToken,
                CredentialStore = store,
            };

            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);
            var ctx = provider();
            ctx.Should().ContainKey("tenant_id");
            ctx["tenant_id"].Should().Be("A");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);

            var payload = result.StructuredContent!.Value;
            payload.GetProperty("totalCount").GetInt32().Should().Be(2);
            payload.GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Should().BeEquivalentTo("order-a1", "order-a3");
        }

        [Fact]
        public async Task TokenExchange_ReachesUserContext_ThroughPerCallProviderSeam()
        {
            // Criterion 5: the exchanged identity reaches QueryIntent.UserContext through the SAME
            // per-tool userContextProvider seam as slices A-C — no new/parallel write path. The
            // store is re-consulted on every provider() invocation (the per-call re-resolution
            // seam), and the resulting identity flows into a real bifrost_query intent.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var store = new FakeExchangeStore(TenantAPrincipal());
            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = () => UpstreamIdpToken,
                CredentialStore = store,
            };

            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);

            provider();
            provider();
            store.Invocations.Should().Be(2,
                "the store is consulted per provider() call — identity is re-resolved each tool call, not captured once");

            // Same seam a tool call drives: the exchanged identity reaches the intent's UserContext.
            var result = await QueryOrdersWith(provider);
            result.IsError.Should().NotBeTrue(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            result.StructuredContent!.Value.GetProperty("rows").EnumerateArray()
                .Select(row => row.GetProperty("name").GetString())
                .Should().BeEquivalentTo("order-a1", "order-a3");
        }

        [Fact]
        public void McpCredentialStore_SourceSynthesizesNoAmbientPrincipalAndHasNoDefaultRegistration()
        {
            // Criterion 4: the store abstraction mirrors IPgCredentialStore's hard rule — no
            // default registration, and no ambient/anonymous principal is ever synthesized inside
            // src/BifrostQL.Mcp. A source scan confirms the adapter only ever RECEIVES a principal
            // (from the host validator / the exchange store) and never mints one, and that it
            // registers no default IMcpCredentialStore that could authenticate everyone to nobody.
            var mcpSrcDir = FindMcpSourceDirectory();
            var files = Directory.EnumerateFiles(mcpSrcDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();
            files.Should().NotBeEmpty("the BifrostQL.Mcp source must be locatable or this guard is vacuous");

            // Tokens that would signal the adapter fabricating an identity (any of these constructs
            // a principal/identity in-process) or wiring a default store registration.
            string[] forbidden =
            {
                "new ClaimsPrincipal", "new ClaimsIdentity", "new GenericPrincipal", "new GenericIdentity",
                "AnonymousPrincipal", "AddSingleton<IMcpCredentialStore", "AddScoped<IMcpCredentialStore",
                "AddTransient<IMcpCredentialStore",
            };

            var offenders = new List<string>();
            foreach (var file in files)
                foreach (var line in File.ReadLines(file))
                    foreach (var token in forbidden)
                        if (line.Contains(token, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)}: '{token}' in: {line.Trim()}");

            offenders.Should().BeEmpty(
                "the store must never synthesize an ambient/anonymous principal and must have no default registration");
        }

        [Fact]
        public async Task TokenExchange_StoreTakesPrecedence_ButAbsentUpstreamCredentialStillFailsClosed()
        {
            // The store exchanges the slice-C extracted upstream credential; an absent credential
            // (unset source) means there is nothing to exchange, so the store is never called and
            // identity fails closed — the opt-in surface cannot be probed without a credential.
            var factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var store = new FakeExchangeStore(TenantAPrincipal());
            var options = new McpAuthOptions
            {
                Mode = McpAuthMode.Bearer,
                CredentialSource = () => null, // no upstream token presented
                CredentialStore = store,
            };

            var provider = BifrostMcpAdapter.CreateUserContextProvider(factory, _host.Services, options);
            provider().Should().BeEmpty("no upstream credential is presented, so no exchange is attempted");
            store.Invocations.Should().Be(0, "an absent credential is never handed to the store");

            var result = await QueryOrdersWith(provider);
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("Tenant context required");
        }

        [Fact]
        public void McpAdapterSource_ContainsNoManualClaimWalking()
        {
            // Criterion 5: identity projection is delegated entirely to
            // IBifrostAuthContextFactory — src/BifrostQL.Mcp must not read claims/issuer itself.
            var mcpSrcDir = FindMcpSourceDirectory();
            var files = Directory.EnumerateFiles(mcpSrcDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();
            files.Should().NotBeEmpty("the BifrostQL.Mcp source must be locatable or this guard is vacuous");

            // Tokens that signal the adapter reading claims/issuer itself rather than handing the
            // whole principal to the factory. 'using' directives (e.g. System.Security.Claims)
            // are not claim walking, so they are skipped.
            string[] forbidden = { ".FindFirst", ".FindAll", ".FindFirstValue", "ClaimTypes.", ".HasClaim", ".Claims" };

            var offenders = new List<string>();
            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                        continue;
                    foreach (var token in forbidden)
                        if (line.Contains(token, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)}: '{token}' in: {line.Trim()}");
                }
            }

            offenders.Should().BeEmpty(
                "identity projection must be delegated entirely to IBifrostAuthContextFactory");
        }

        /// <summary>
        /// Walks up from the test assembly location to the repository root (the directory holding
        /// BifrostQL.sln) and returns its <c>src/BifrostQL.Mcp</c> directory. Throws if it cannot
        /// be found so the source-scan guard fails loudly rather than passing vacuously.
        /// </summary>
        private static string FindMcpSourceDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "BifrostQL.Mcp");
                if (File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")) && Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate src/BifrostQL.Mcp from {AppContext.BaseDirectory}");
        }

        private sealed record LogEntry(LogLevel Level, string Message);

        /// <summary>
        /// Minimal <see cref="ILoggerFactory"/> that records every log entry so a test can assert
        /// on the startup posture warning without engaging the stdio transport.
        /// </summary>
        private sealed class CapturingLoggerFactory : ILoggerFactory
        {
            public List<LogEntry> Entries { get; } = new();
            public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }

            private sealed class CapturingLogger : ILogger
            {
                private readonly List<LogEntry> _entries;
                public CapturingLogger(List<LogEntry> entries) => _entries = entries;
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                    Exception? exception, Func<TState, Exception?, string> formatter)
                    => _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
