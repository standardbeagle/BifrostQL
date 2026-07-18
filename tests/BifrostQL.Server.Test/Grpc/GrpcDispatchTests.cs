using BifrostQL.Server.Grpc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criteria 3 &amp; 4: Get/List/Stream dispatch builds a programmatic read intent and executes it
    /// through the REAL <c>IQueryIntentExecutor</c> pipeline over seeded SQLite — proven by the rows
    /// coming back AND by the pipeline's generated SQL being captured (the adapter never touches SQL
    /// itself). A server-streaming List is hard-bounded by <c>MaxStreamRows</c>. Composite-key Get
    /// binds all PK columns.
    /// </summary>
    public sealed class GrpcDispatchTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        private static readonly string[] MetadataRules = Array.Empty<string>();

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO widgets(id, name) VALUES (1,'first'),(2,'second'),(3,'third'),(4,'fourth'),(5,'fifth')",
            "DROP TABLE IF EXISTS order_lines",
            "CREATE TABLE order_lines (order_id INTEGER NOT NULL, line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no))",
            "INSERT INTO order_lines(order_id, line_no, qty) VALUES (10,1,7),(10,2,9),(20,1,3)",
        };

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(
                nameof(GrpcDispatchTests), MetadataRules, SeedSql,
                new GrpcWireOptions { MaxStreamRows = 3 });
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static global::Grpc.Core.Metadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");

        [Fact]
        public async Task Get_by_primary_key_returns_the_row_through_the_pipeline()
        {
            var row = await _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = 2 }, User());

            row.Should().NotBeNull();
            row!["name"].Should().Be("second");

            // The read travelled the real pipeline — the SQL it generated was captured.
            _harness.CapturedSql("widgets").Should().NotBeEmpty();
        }

        [Fact]
        public async Task Get_with_a_missing_key_is_not_found_null()
        {
            var row = await _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = 999 }, User());
            row.Should().BeNull();
        }

        [Fact]
        public async Task Get_binds_all_columns_of_a_composite_primary_key()
        {
            var row = await _client.GetAsync(
                "order_lines",
                new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 2 },
                User());

            row.Should().NotBeNull();
            Convert.ToInt32(row!["qty"]).Should().Be(9);

            // A composite key must AND all columns: the (10,1) row must NOT match (10,2).
            var other = await _client.GetAsync(
                "order_lines",
                new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 1 },
                User());
            Convert.ToInt32(other!["qty"]).Should().Be(7);
        }

        [Fact]
        public async Task List_returns_rows_through_the_pipeline()
        {
            var rows = await _client.ListAsync("widgets", User());

            rows.Select(r => r["name"]).Should().Contain(new object?[] { "first", "second", "third" });
            _harness.CapturedSql("widgets").Should().NotBeEmpty();
        }

        [Fact]
        public async Task Stream_is_bounded_by_the_configured_max_row_count()
        {
            // Five rows exist, MaxStreamRows is 3 — the stream can never emit unbounded rows.
            var rows = await _client.StreamAsync("widgets", User());

            rows.Should().HaveCount(3);
        }
    }
}
