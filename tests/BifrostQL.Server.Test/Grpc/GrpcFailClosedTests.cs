using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// The load-bearing security criterion: reads are wired through <c>IBifrostAuthContextFactory</c>
    /// + <c>IQueryIntentExecutor</c> and are FAIL-CLOSED even though bearer identity is a later slice.
    /// A call with NO identity resolves to an empty user context, which the pipeline treats as
    /// fail-closed on a tenant-scoped table — it gets a denial, NEVER another tenant's or the global
    /// unfiltered rows. Two tenants driving the same RPCs see disjoint rows, with the tenant predicate
    /// bound as a parameter in the generated SQL.
    /// </summary>
    public sealed class GrpcFailClosedTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL)",
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (1,'tenant-a','a-first'),(2,'tenant-a','a-second'),
                (3,'tenant-b','b-first'),(4,'tenant-b','b-second'),(5,'tenant-b','b-third')
            """,
        };

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(nameof(GrpcFailClosedTests), MetadataRules, SeedSql);
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata Anonymous() => new();
        private static Metadata Tenant(string tenant) => GrpcRealDbHarness.Identity("u", tenant, "member");

        [Fact]
        public async Task Anonymous_list_on_a_tenant_table_is_denied_not_open()
        {
            // No identity → empty user context → the tenant filter fails closed. Crucially it does NOT
            // return the unfiltered table.
            var act = () => _client.ListAsync("orders", Anonymous());

            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
            // The sanitized status must not leak the table/tenant-key detail (invariant 3).
            ex.Status.Detail.Should().NotContain("orders").And.NotContain("tenant_id");
        }

        [Fact]
        public async Task Anonymous_get_on_a_tenant_table_is_denied_not_open()
        {
            var act = () => _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 1 }, Anonymous());

            (await act.Should().ThrowAsync<RpcException>())
                .Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        }

        [Fact]
        public async Task Two_tenants_see_disjoint_rows()
        {
            var a = (await _client.ListAsync("orders", Tenant("tenant-a"))).Select(r => Convert.ToInt32(r["id"])).ToList();
            var b = (await _client.ListAsync("orders", Tenant("tenant-b"))).Select(r => Convert.ToInt32(r["id"])).ToList();

            a.Should().BeEquivalentTo(new[] { 1, 2 });
            b.Should().BeEquivalentTo(new[] { 3, 4, 5 });
            a.Should().NotIntersectWith(b);
        }

        [Fact]
        public async Task Cross_tenant_get_is_indistinguishable_from_a_missing_row()
        {
            // tenant-a asks for row 3 (tenant-b) — scoped away → null, exactly like a missing key.
            var crossTenant = await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 3 }, Tenant("tenant-a"));
            crossTenant.Should().BeNull();

            var own = await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 1 }, Tenant("tenant-a"));
            own!["name"].Should().Be("a-first");
        }

        [Fact]
        public async Task Tenant_predicate_binds_as_a_parameter_in_generated_sql()
        {
            await _client.ListAsync("orders", Tenant("tenant-a"));

            var sql = _harness.CapturedSql("orders");
            sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
            sql.Should().NotContain("tenant-a", "tenant values must bind as parameters");
            sql.Should().Contain("@");
        }
    }
}
