using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Prometheus
{
    /// <summary>
    /// Criterion 1 — a Prometheus scrape's business series are scoped by the SAME unskippable
    /// transformer chain every row read crosses, proven end-to-end through the mounted
    /// <c>/metrics</c> endpoint → the slice-2 collector → <c>IQueryIntentExecutor</c>:
    /// <list type="bullet">
    /// <item><b>tenant-filter</b> — an aggregate-mode tenant metric counts only the fixed service
    /// identity's tenant, never a cross-tenant total.</item>
    /// <item><b>soft-delete</b> — soft-deleted rows are invisible to the aggregate.</item>
    /// <item><b>policy</b> — a policy read-denied table is excluded fail-closed (surfaced only as a
    /// health metric), its data never on the wire.</item>
    /// <item><b>parameterized execution</b> — the scoped aggregate SQL binds its identity value as a
    /// parameter; no raw value is concatenated into the statement.</item>
    /// </list>
    /// The scope value is never a predicate the adapter built — the collector supplies only the
    /// resolved identity and the transformers narrow the aggregate.
    /// </summary>
    public sealed class PrometheusIntentSeamIntegrationTests
    {
        // Sales: non-tenant, soft-delete. Orders: tenant-scoped, aggregate mode (runs under the fixed
        // service identity). Documents: policy read-denied entirely (policy metadata present, no read
        // grant) → excluded for the non-admin scrape identity.
        private static readonly string[] Metadata =
        {
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount; soft-delete: deleted_at }",
            "main.Orders { metric-name: orders_total; metric-count: enabled; tenant-filter: tenant_id; metric-security-mode: aggregate }",
            "main.Documents { metric-name: documents_total; metric-count: enabled; policy-read-deny: body }",
        };

        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, amount REAL NOT NULL, deleted_at TEXT NULL);",
            // Two live rows (amount 10 + 5 = 15) and one soft-deleted row (amount 100) that must vanish.
            "INSERT INTO Sales(id, amount, deleted_at) VALUES (1, 10.0, NULL), (2, 5.0, NULL), (3, 100.0, '2020-01-01');",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, name TEXT NOT NULL);",
            // Tenant 1 has 2 rows; tenant 2 has 3. The scrape identity is tenant 1 → must count 2.
            "INSERT INTO Orders(id, tenant_id, name) VALUES (1, 1, 'a'), (2, 1, 'b'), (3, 2, 'c'), (4, 2, 'd'), (5, 2, 'e');",
            "CREATE TABLE Documents (id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL);",
            "INSERT INTO Documents(id, title, body) VALUES (1, 'public', 'classified');",
        };

        [Fact]
        public async Task Soft_deleted_rows_are_excluded_from_the_aggregate()
        {
            await using var host = await PrometheusScrapeHost.StartAsync(
                "seam-softdelete", Metadata, Seed, serviceIdentity: PrometheusScrapeHost.ServicePrincipal("1"));

            var body = await host.ScrapeBodyAsync();

            // COUNT excludes the soft-deleted row; SUM(amount) is 15, not 115.
            body.Should().Contain("sales_total 2\n");
            body.Should().Contain("sales_total_sum 15\n");
        }

        [Fact]
        public async Task An_aggregate_mode_tenant_metric_counts_only_the_service_identitys_tenant()
        {
            await using var host = await PrometheusScrapeHost.StartAsync(
                "seam-tenant", Metadata, Seed, serviceIdentity: PrometheusScrapeHost.ServicePrincipal("1"));

            var body = await host.ScrapeBodyAsync();

            // Tenant 1 has 2 rows; a cross-tenant total would be 5. The tenant-filter transformer
            // scoped the aggregate to the fixed service identity's tenant.
            body.Should().Contain("orders_total 2\n");
            body.Should().NotContain("orders_total 5\n");
        }

        [Fact]
        public async Task A_tenant_metric_with_no_service_identity_is_excluded_fail_closed()
        {
            // Armed, but NO service identity configured → the tenant metric has no scoping authority
            // and is excluded rather than run under an anonymous/global context.
            await using var host = await PrometheusScrapeHost.StartAsync(
                "seam-noidentity", Metadata, Seed, serviceIdentity: null);

            var body = await host.ScrapeBodyAsync();

            body.Should().NotContain("orders_total ");     // no business series
            body.Should().Contain("sales_total 2\n");       // the non-tenant metric still collects
        }

        [Fact]
        public async Task A_policy_read_denied_table_is_excluded_and_its_data_never_reaches_the_wire()
        {
            await using var host = await PrometheusScrapeHost.StartAsync(
                "seam-policy", Metadata, Seed, serviceIdentity: PrometheusScrapeHost.ServicePrincipal("1"));

            var body = await host.ScrapeBodyAsync();

            // The policy transformer denied the read → surfaced ONLY as a health metric, no business
            // series, and no internal denial detail on the scrape wire.
            body.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"documents_total\"} 1\n");
            body.Should().NotContain("documents_total ");
            body.Should().NotContain("classified");
            body.Should().NotContain("Access denied");
        }

        [Fact]
        public async Task The_scoped_aggregate_binds_its_identity_value_as_a_parameter_not_raw_sql()
        {
            var recorder = new RecordingQueryObserver();
            await using var host = await PrometheusScrapeHost.StartAsync(
                "seam-param", Metadata, Seed,
                serviceIdentity: PrometheusScrapeHost.ServicePrincipal("1"),
                observers: new[] { recorder });

            using var response = await host.Client.SendAsync(host.Scrape());
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var ordersSql = recorder.SqlFor("Orders");
            ordersSql.Should().NotBeNull("the tenant aggregate executed through the intent seam");
            // Scoped on tenant_id, but the tenant VALUE is bound as a @p parameter — never inlined.
            ordersSql.Should().Contain("tenant_id");
            ordersSql.Should().Contain("@p");
        }
    }
}
