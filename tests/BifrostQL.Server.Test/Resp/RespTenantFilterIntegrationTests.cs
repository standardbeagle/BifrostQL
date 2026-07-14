using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The load-bearing security property of the whole RESP epic: the tenant-filter transformer is
    /// applied on the ACTUAL RESP wire path exactly as on GraphQL, across every read command shape —
    /// GET (single-key JSON), HGETALL (row as field/value hash), and SCAN (key enumeration). Two
    /// identities in different tenants drive the identical commands over real RESP sessions against a
    /// real transformer-pipeline executor over seeded SQLite, and each sees ONLY its own tenant's rows —
    /// disjoint sets, with the tenant predicate present in the generated SQL and bound as a parameter.
    /// Driven end to end through the wire; would fail if the tenant filter were removed.
    /// </summary>
    public sealed class RespTenantFilterIntegrationTests : IAsyncLifetime
    {
        private RespRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS orders",
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL
            )
            """,
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (1, 'tenant-a', 'a-first'),
                (2, 'tenant-a', 'a-second'),
                (3, 'tenant-b', 'b-first'),
                (4, 'tenant-b', 'b-second'),
                (5, 'tenant-b', 'b-third')
            """,
        };

        public async Task InitializeAsync()
            => _harness = await RespRealDbHarness.StartAsync(nameof(RespTenantFilterIntegrationTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        [Fact]
        public async Task Scan_TwoTenants_EnumerateDisjointKeySets()
        {
            var keysA = await _harness.ScanKeysAsync(TenantPrincipal("user-a", "tenant-a"), "orders");
            var keysB = await _harness.ScanKeysAsync(TenantPrincipal("user-b", "tenant-b"), "orders");

            // SCAN emits only PKs of rows the identity may see — the pipeline's tenant scope ANDs onto
            // the keyset predicate, so the two enumerations are disjoint.
            keysA.Should().BeEquivalentTo("orders:1", "orders:2");
            keysB.Should().BeEquivalentTo("orders:3", "orders:4", "orders:5");
            keysA.Should().NotIntersectWith(keysB);
        }

        [Fact]
        public async Task HGetAll_OwnTenantRow_IsVisible_OtherTenantRow_IsEmpty()
        {
            var tenantA = TenantPrincipal("user-a", "tenant-a");

            // tenant-a sees its own row 1 as a full field/value hash…
            var own = await _harness.HGetAllAsync(tenantA, "orders:1");
            own.Should().Contain("name", "a-first").And.Contain("tenant_id", "tenant-a");

            // …but row 3 (tenant-b) is indistinguishable from a missing key — an empty hash, no leak.
            var crossTenant = await _harness.HGetAllAsync(tenantA, "orders:3");
            crossTenant.Should().BeEmpty("a row in another tenant must be invisible over the wire");
        }

        [Fact]
        public async Task Get_OwnTenantRow_IsJson_OtherTenantRow_IsNull()
        {
            var tenantB = TenantPrincipal("user-b", "tenant-b");

            var own = await _harness.GetAsync(tenantB, "orders:3");
            own.Should().NotBeNull().And.Contain("\"name\":\"b-first\"");

            var crossTenant = await _harness.GetAsync(tenantB, "orders:1");
            crossTenant.Should().BeNull("a row in another tenant must be indistinguishable from a missing key");
        }

        [Fact]
        public async Task TenantPredicate_IsInjectedIntoGeneratedSql_AndBindsAsParameter()
        {
            await _harness.HGetAllAsync(TenantPrincipal("user-a", "tenant-a"), "orders:1");

            var sql = _harness.CapturedSql("orders");

            // The tenant predicate was injected by the transformer pipeline (the wire only asked for a PK)…
            sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
            // …and the tenant value binds as a parameter, never concatenated into the SQL text.
            sql.Should().NotContain("tenant-a", "tenant values must bind as parameters, never concatenate");
            sql.Should().Contain("@", "the WHERE clause must reference bound parameters");
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "resp"));
    }
}
