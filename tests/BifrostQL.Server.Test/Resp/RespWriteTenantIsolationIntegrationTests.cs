using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The write-side counterpart to <see cref="RespTenantFilterIntegrationTests"/>: two identities in
    /// different tenants drive SET/HSET over real RESP sessions against the real transformer-pipeline
    /// mutation executor over seeded SQLite. The pipeline's tenant predicate narrows a cross-tenant write
    /// to ZERO rows — so the data is always safe — but the WIRE MUST NOT LIE about it: a scoped-away SET
    /// replies RESP nil (never +OK) and a scoped-away HSET replies 0, indistinguishable from a missing
    /// row exactly as GET reports a hidden row. A real in-scope write still replies +OK / the field count.
    /// Would fail if the adapter reported success on a zero-affected write (reading the update intent's
    /// <c>Value</c> — the primary KEY on a single-key table — as an affected-row count).
    /// </summary>
    public sealed class RespWriteTenantIsolationIntegrationTests : IAsyncLifetime
    {
        private RespRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "*.accounts { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS accounts",
            """
            CREATE TABLE accounts (
                id INTEGER PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL
            )
            """,
            """
            INSERT INTO accounts(id, tenant_id, name) VALUES
                (1, 'tenant-a', 'a-one'),
                (2, 'tenant-b', 'b-two')
            """,
        };

        public async Task InitializeAsync()
            => _harness = await RespRealDbHarness.StartAsync(
                nameof(RespWriteTenantIsolationIntegrationTests), MetadataRules, SeedSql, enableWrites: true);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        [Fact]
        public async Task Set_CrossTenant_IsZeroAffected_RepliesNil_RowUnchanged()
        {
            var tenantA = TenantPrincipal("user-a", "tenant-a");
            var tenantB = TenantPrincipal("user-b", "tenant-b");

            // tenant-a targets accounts:2, which belongs to tenant-b. The pipeline narrows it to zero rows.
            var wrote = await _harness.SetAsync(tenantA, "accounts:2", "{\"name\":\"hacked\"}");

            wrote.Should().BeFalse(
                "a scoped-away update affects zero rows and must reply RESP nil, never +OK — the update " +
                "intent's Value is the primary KEY on a single-key table, not an affected-row count");

            // tenant-b reads its own row back over the wire: the pipeline never wrote it.
            var json = await _harness.GetAsync(tenantB, "accounts:2");
            json.Should().Contain("\"name\":\"b-two\"", "the out-of-scope row must be untouched");
        }

        [Fact]
        public async Task Set_OwnTenant_RealWrite_RepliesOk_RowChanged()
        {
            var tenantA = TenantPrincipal("user-a", "tenant-a");

            var wrote = await _harness.SetAsync(tenantA, "accounts:1", "{\"name\":\"a-renamed\"}");

            wrote.Should().BeTrue("an in-scope update affects one row and replies +OK");
            var json = await _harness.GetAsync(tenantA, "accounts:1");
            json.Should().Contain("\"name\":\"a-renamed\"", "the in-scope update was applied by the pipeline");
        }

        [Fact]
        public async Task HSet_CrossTenant_IsZeroAffected_RepliesZero_RowUnchanged()
        {
            var tenantA = TenantPrincipal("user-a", "tenant-a");
            var tenantB = TenantPrincipal("user-b", "tenant-b");

            var count = await _harness.HSetAsync(
                tenantA, "accounts:2", new Dictionary<string, object?> { ["name"] = "hacked" });

            count.Should().Be(0, "a scoped-away HSET writes no field and must report 0, indistinguishable from a missing row");

            var json = await _harness.GetAsync(tenantB, "accounts:2");
            json.Should().Contain("\"name\":\"b-two\"", "the out-of-scope row must be untouched");
        }

        [Fact]
        public async Task HSet_OwnTenant_RealWrite_ReportsFieldCount()
        {
            var tenantB = TenantPrincipal("user-b", "tenant-b");

            var count = await _harness.HSetAsync(
                tenantB, "accounts:2", new Dictionary<string, object?> { ["name"] = "b-renamed" });

            count.Should().Be(1, "an in-scope HSET reports the number of fields written");
            var json = await _harness.GetAsync(tenantB, "accounts:2");
            json.Should().Contain("\"name\":\"b-renamed\"");
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "resp"));
    }
}
