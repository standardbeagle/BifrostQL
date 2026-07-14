using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// The load-bearing security property of the whole pgwire epic: the tenant-filter
    /// transformer is applied on the ACTUAL pgwire wire path exactly as on GraphQL. Two
    /// identities in different tenants issue the identical SELECT over a real cleartext
    /// pgwire session against a real transformer-pipeline executor over seeded SQLite, and
    /// each sees ONLY its own tenant's rows — disjoint result sets, with the tenant predicate
    /// present in the generated SQL and both the tenant and any caller value bound as
    /// parameters (never concatenated). Driven end to end through the wire, not the read seam.
    /// </summary>
    public sealed class PgWireTenantFilterIntegrationTests : IAsyncLifetime
    {
        private PgWireRealDbHarness _harness = null!;

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
            => _harness = await PgWireRealDbHarness.StartAsync(nameof(PgWireTenantFilterIntegrationTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        [Fact]
        public async Task SameSelect_TwoTenants_SeeDisjointRowSetsOverTheWire()
        {
            const string sql = "SELECT id, tenant_id, name FROM orders";

            var tenantA = await _harness.QueryAsync(TenantPrincipal("user-a", "tenant-a"), sql);
            var tenantB = await _harness.QueryAsync(TenantPrincipal("user-b", "tenant-b"), sql);

            tenantA.HasError.Should().BeFalse();
            tenantB.HasError.Should().BeFalse();

            // Each identity sees ONLY its own tenant's rows — the wire path applied the
            // tenant filter, not the query text (the caller asked for the whole table).
            var namesA = tenantA.Rows.Select(r => r[2]).ToList();
            var namesB = tenantB.Rows.Select(r => r[2]).ToList();
            namesA.Should().BeEquivalentTo(new[] { "a-first", "a-second" });
            namesB.Should().BeEquivalentTo(new[] { "b-first", "b-second", "b-third" });

            // Every returned row carries the caller's own tenant; the result sets are disjoint.
            tenantA.Rows.Should().OnlyContain(r => r[1] == "tenant-a");
            tenantB.Rows.Should().OnlyContain(r => r[1] == "tenant-b");
            namesA.Should().NotIntersectWith(namesB);
        }

        [Fact]
        public async Task TenantPredicate_IsInjectedIntoGeneratedSql_AndBindsAsParameter()
        {
            await _harness.QueryAsync(TenantPrincipal("user-a", "tenant-a"),
                "SELECT id, name FROM orders WHERE name = 'a-second'");

            var sql = _harness.CapturedSql("orders");

            // The tenant predicate was injected by the transformer pipeline…
            sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
            // …and neither the tenant value nor the caller's filter value appears in the SQL
            // text: both bind as @p parameters.
            sql.Should().NotContain("tenant-a", "tenant values must bind as parameters, never concatenate");
            sql.Should().NotContain("a-second", "caller values must bind as parameters, never concatenate");
            sql.Should().Contain("@", "the WHERE clause must reference bound parameters");
        }

        [Fact]
        public async Task CallerFilter_ComposesWithTenantScope_NeverEscapesIt()
        {
            // tenant-b asks for a row name that only exists in tenant-a: the caller filter
            // ANDs with the tenant predicate, so it matches nothing — the filter cannot be
            // used to read across the tenant boundary.
            var crossTenant = await _harness.QueryAsync(TenantPrincipal("user-b", "tenant-b"),
                "SELECT id, name FROM orders WHERE name = 'a-first'");

            crossTenant.HasError.Should().BeFalse();
            crossTenant.Rows.Should().BeEmpty("a caller filter must not reach another tenant's rows");

            // The same filter within the caller's own tenant does match.
            var ownTenant = await _harness.QueryAsync(TenantPrincipal("user-b", "tenant-b"),
                "SELECT id, name FROM orders WHERE name = 'b-first'");
            ownTenant.Rows.Should().ContainSingle().Which[1].Should().Be("b-first");
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "pgwire"));
    }
}
