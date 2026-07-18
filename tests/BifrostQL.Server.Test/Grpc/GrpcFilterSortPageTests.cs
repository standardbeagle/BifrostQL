using BifrostQL.Server.Grpc;
using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// gRPC slice 5 end-to-end over the REAL pipeline + seeded SQLite: the List/Stream filter/sort/page
    /// compiler. Proves the documented operators become bound SQL parameters, names are schema-derived
    /// (unknown → INVALID_ARGUMENT), caps are enforced, the adapter filter is AND-composed with tenant
    /// scope (never widens it), List and Stream share ONE compiler (equivalent ordered rows), and a
    /// forged/cross-tenant page token can never escape the caller's visible set.
    /// </summary>
    public sealed class GrpcFilterSortPageTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)",
            "INSERT INTO widgets(id, name, qty) VALUES (1,'first',10),(2,'second',20),(3,'third',30),(4,'fourth',40),(5,'fifth',50)",
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL)",
            @"INSERT INTO orders(id, tenant_id, name) VALUES
                (1,'tenant-a','a-first'),(2,'tenant-a','a-second'),
                (3,'tenant-b','b-first'),(4,'tenant-b','b-second'),(5,'tenant-b','b-third')",
            "DROP TABLE IF EXISTS order_lines",
            "CREATE TABLE order_lines (order_id INTEGER NOT NULL, line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no))",
            "INSERT INTO order_lines(order_id, line_no, qty) VALUES (10,1,7),(10,2,9),(20,1,3)",
        };

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(nameof(GrpcFilterSortPageTests), MetadataRules, SeedSql);
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");
        private static Metadata Tenant(string tenant) => GrpcRealDbHarness.Identity("u", tenant, "member");

        // ---- criterion 1: operators, parameterization, schema-derived names ----

        [Fact]
        public async Task Filter_selects_matching_rows_and_binds_the_literal_as_a_parameter()
        {
            // SQLite INTEGER maps to the string wire kind (as row values do), so the literal is a
            // JSON string bound as a text parameter and cast by the dialect.
            var rows = await _client.ListAsync("widgets", User(), filter: """{ "qty": { "_gte": "30" } }""");

            rows.Select(r => Convert.ToInt32(r["id"])).Should().BeEquivalentTo(new[] { 3, 4, 5 });

            // Parameterized: the literal is bound, never spliced into the generated SQL text.
            var sql = _harness.CapturedSql("widgets");
            sql.Should().NotContain("30");
        }

        [Fact]
        public async Task In_operator_selects_the_listed_values()
        {
            var rows = await _client.ListAsync("widgets", User(), filter: """{ "id": { "_in": ["1", "3", "5"] } }""");
            rows.Select(r => Convert.ToInt32(r["id"])).Should().BeEquivalentTo(new[] { 1, 3, 5 });
        }

        [Fact]
        public async Task Unknown_field_is_invalid_argument()
        {
            var act = () => _client.ListAsync("widgets", User(), filter: """{ "nope": { "_eq": 1 } }""");
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }

        [Fact]
        public async Task Unknown_operator_is_invalid_argument()
        {
            var act = () => _client.ListAsync("widgets", User(), filter: """{ "qty": { "_like": "x" } }""");
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }

        // ---- criterion 2: caps → clean INVALID_ARGUMENT, never a crash ----

        [Fact]
        public async Task An_over_deep_filter_is_invalid_argument_not_a_crash()
        {
            var deep = string.Concat(Enumerable.Repeat("""{ "and": [ """, GrpcReadCaps.MaxFilterDepth + 3))
                + """{ "qty": { "_eq": "1" } }"""
                + string.Concat(Enumerable.Repeat(" ] }", GrpcReadCaps.MaxFilterDepth + 3));

            var act = () => _client.ListAsync("widgets", User(), filter: deep);
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }

        // ---- criterion 3: filter AND-composed with tenant, never widens scope ----

        [Fact]
        public async Task A_filter_cannot_widen_past_tenant_scope()
        {
            // tenant-a filters for a row that belongs to tenant-b: the adapter predicate is ANDed with
            // the tenant predicate, so it matches nothing — the filter can only narrow within scope.
            var rows = await _client.ListAsync(
                "orders", Tenant("tenant-a"), filter: """{ "name": { "_eq": "b-first" } }""");

            rows.Should().BeEmpty();

            var sql = _harness.CapturedSql("orders");
            sql.Should().Contain("tenant_id");          // tenant predicate still present
            sql.Should().NotContain("b-first");         // caller literal bound as a parameter
        }

        // ---- criterion 4: List and Stream share ONE compiler ----

        [Fact]
        public async Task List_and_stream_yield_the_same_ordered_rows_for_the_same_request()
        {
            const string filter = """{ "id": { "_gte": "2" } }""";
            const string orderBy = "id desc";

            var listRows = await _client.ListAsync("widgets", User(), filter: filter, orderBy: orderBy, pageSize: 10);
            var streamRows = await _client.StreamAsync(
                "widgets", User(), default, filter: filter, orderBy: orderBy, pageSize: 10);

            var listIds = listRows.Select(r => Convert.ToInt32(r["id"])).ToList();
            var streamIds = streamRows.Select(r => Convert.ToInt32(r["id"])).ToList();

            listIds.Should().Equal(5, 4, 3, 2);          // ordered descending
            streamIds.Should().Equal(listIds);            // identical rows AND identical order
        }

        // ---- criterion 3: cursor is position-only, re-scoped by the live pipeline ----

        [Fact]
        public async Task Paging_walks_only_the_callers_own_scope()
        {
            // tenant-a has exactly rows 1 and 2. Page size 1, walk via the continuation token.
            var page1 = await _client.ListPageAsync("orders", Tenant("tenant-a"), pageSize: 1);
            page1.Rows.Select(r => Convert.ToInt32(r["id"])).Should().Equal(1);
            page1.NextPageToken.Should().NotBeNull();

            var page2 = await _client.ListPageAsync("orders", Tenant("tenant-a"), pageSize: 1, pageToken: page1.NextPageToken);
            page2.Rows.Select(r => Convert.ToInt32(r["id"])).Should().Equal(2);

            // Every id tenant-a ever sees is its own — never tenant-b's (3,4,5).
            var seen = page1.Rows.Concat(page2.Rows).Select(r => Convert.ToInt32(r["id"]));
            seen.Should().OnlyContain(id => id == 1 || id == 2);
        }

        [Fact]
        public async Task A_page_token_minted_for_another_tenant_cannot_be_replayed()
        {
            // tenant-a mints a valid continuation token...
            var page1 = await _client.ListPageAsync("orders", Tenant("tenant-a"), pageSize: 1);
            page1.NextPageToken.Should().NotBeNull();

            // ...tenant-b replays it: the identity fingerprint in the binding differs, so the token
            // fails its integrity check — it can never be used to resume in another tenant's scope.
            var act = () => _client.ListPageAsync("orders", Tenant("tenant-b"), pageSize: 1, pageToken: page1.NextPageToken);
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }

        [Fact]
        public async Task A_garbage_page_token_is_invalid_argument()
        {
            var act = () => _client.ListAsync("widgets", User(), pageToken: "not-a-real-token");
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        }

        // ---- composite-PK sort/cursor ----

        [Fact]
        public async Task Composite_key_table_sorts_and_pages_by_the_full_key()
        {
            // Order by qty ascending; the full composite key is the deterministic tiebreak.
            var rows = await _client.ListAsync("order_lines", User(), orderBy: "qty asc");
            rows.Select(r => Convert.ToInt32(r["qty"])).Should().Equal(3, 7, 9);

            // Page size 1 walks all three rows across the composite key without collision.
            var walked = new List<int>();
            string? token = null;
            for (var i = 0; i < 3; i++)
            {
                var page = await _client.ListPageAsync("order_lines", User(), pageSize: 1, pageToken: token);
                page.Rows.Should().HaveCount(1);
                walked.Add(Convert.ToInt32(page.Rows[0]["order_id"]) * 100 + Convert.ToInt32(page.Rows[0]["line_no"]));
                token = page.NextPageToken;
            }
            // (10,1),(10,2),(20,1) in full-key ascending order — never an index-zero collision.
            walked.Should().Equal(1001, 1002, 2001);
        }
    }
}
