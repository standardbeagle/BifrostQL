using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Test.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Slice-3 scrape SCOPE decision (criteria 1/3/4). The resolver turns each metric's slice-1
    /// <c>metric-security-mode</c> into the user context its aggregate runs under — a fixed service
    /// identity (<c>aggregate</c>) or declared tenant-label partitioning (<c>per-tenant</c>) — and
    /// fails CLOSED on every misconfiguration. The no-cross-tenant-leakage proofs run the resolved
    /// scope through the real slice-2 collector on seeded SQLite, so scoping is proven end to end,
    /// not asserted on a shape.
    /// </summary>
    public sealed class PrometheusScrapeScopeResolverTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, amount) VALUES (1, 'west', 10.0), (2, 'east', 2.0);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, status TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, status, amount) VALUES " +
                "(1, 'tenant-a', 'open', 100.0), (2, 'tenant-a', 'open', 50.0), (3, 'tenant-a', 'closed', 25.0), " +
                "(4, 'tenant-b', 'open', 999.0), (5, 'tenant-b', 'closed', 888.0);",
        };

        private const string NonTenant =
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount; metric-labels: region }";
        private const string AggregateMode =
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: status; metric-security-mode: aggregate }";
        private const string PerTenantMode =
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: tenant_id, status; metric-security-mode: per-tenant }";
        private const string PerTenantNoLabel =
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: status; metric-security-mode: per-tenant }";

        private static ClaimsPrincipal ServicePrincipal(string tenant) =>
            new(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "prometheus-service"),
                    new Claim(LocalAuthClaims.Tenant, tenant),
                },
                authenticationType: "test"));

        private static PrometheusScrapeScopeResolver Resolver(ClaimsPrincipal? serviceIdentity) =>
            new(new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = true,
                ScrapeCredential = "token",
                ServiceIdentity = serviceIdentity,
            });

        // ---- criterion 1/3: non-tenant metric runs ungated ---------------------------------

        [Fact]
        public async Task A_non_tenant_metric_is_included_with_an_empty_context()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-nt", new[] { NonTenant }, Seed);
            var table = (await harness.ModelAsync()).GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var scope = Resolver(serviceIdentity: null).ResolveScope(config, table);

            scope.IsIncluded.Should().BeTrue();
            scope.UserContext.Should().BeEmpty();
        }

        // ---- criterion 3: aggregate mode = fixed service identity as scoping authority -----

        [Fact]
        public async Task Aggregate_mode_projects_the_service_identity_context()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-agg-ctx", new[] { AggregateMode }, Seed);
            var table = (await harness.ModelAsync()).GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var scope = Resolver(ServicePrincipal("tenant-a")).ResolveScope(config, table);

            scope.IsIncluded.Should().BeTrue();
            scope.UserContext!.Should().ContainKey("tenant_id").WhoseValue.Should().Be("tenant-a");
        }

        [Fact]
        public async Task Aggregate_mode_scopes_the_aggregate_to_the_service_identitys_tenant()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-agg", new[] { AggregateMode }, Seed);
            var model = await harness.ModelAsync();
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);
            var collector = new PrometheusSeriesCollector(harness.Reads);

            var scope = Resolver(ServicePrincipal("tenant-a")).ResolveScope(config, table);
            var series = await collector.CollectAsync(config, table, ODataRealDbHarness.EndpointPath, scope.UserContext!);

            // Only tenant-a's rows: open(2/150) + closed(1/25). tenant-b's 999/888 MUST be absent —
            // NO cross-tenant leakage. The global total would be 5 rows / 2062.0.
            series.Samples.Sum(s => s.Count!.Value).Should().Be(3);
            series.Samples.Sum(s => s.Sum!.Value).Should().Be(175d);
        }

        [Fact]
        public async Task Aggregate_mode_scoping_authority_swaps_with_the_service_identity()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-agg-b", new[] { AggregateMode }, Seed);
            var model = await harness.ModelAsync();
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);
            var collector = new PrometheusSeriesCollector(harness.Reads);

            // A service identity scoped to tenant-b sees ONLY tenant-b — proving the fixed identity
            // is the scoping authority, not an ambient global view.
            var scope = Resolver(ServicePrincipal("tenant-b")).ResolveScope(config, table);
            var series = await collector.CollectAsync(config, table, ODataRealDbHarness.EndpointPath, scope.UserContext!);

            series.Samples.Sum(s => s.Count!.Value).Should().Be(2);
            series.Samples.Sum(s => s.Sum!.Value).Should().Be(1887d); // 999 + 888
        }

        // ---- criterion 3/4: per-tenant mode partitions by the tenant label -----------------

        [Fact]
        public async Task Per_tenant_mode_partitions_every_series_by_the_tenant_label()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-pt", new[] { PerTenantMode }, Seed);
            var model = await harness.ModelAsync();
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);
            var collector = new PrometheusSeriesCollector(harness.Reads);

            var scope = Resolver(ServicePrincipal("tenant-a")).ResolveScope(config, table);
            scope.IsIncluded.Should().BeTrue();

            var series = await collector.CollectAsync(config, table, ODataRealDbHarness.EndpointPath, scope.UserContext!);

            // EVERY series carries a tenant_id label = tenant-a: there is no un-partitioned total a
            // scraper could read as a cross-tenant blend, and tenant-b never appears.
            series.Samples.Should().OnlyContain(s =>
                s.Labels.Any(l => l.Key == "tenant_id" && l.Value == "tenant-a"));
            series.Samples.Should().NotContain(s =>
                s.Labels.Any(l => l.Key == "tenant_id" && l.Value == "tenant-b"));
            series.Samples.Sum(s => s.Count!.Value).Should().Be(3);
        }

        [Fact]
        public async Task Per_tenant_mode_rejects_a_table_whose_tenant_column_is_not_a_declared_label()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-pt-nolabel", new[] { PerTenantNoLabel }, Seed);
            var table = (await harness.ModelAsync()).GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var scope = Resolver(ServicePrincipal("tenant-a")).ResolveScope(config, table);

            // Not partitionable → EXCLUDED, never silently aggregated cross-tenant.
            scope.IsIncluded.Should().BeFalse();
            scope.UserContext.Should().BeNull();
            scope.Reason.Should().Contain("declared metric label");
        }

        // ---- criterion 4: no anonymous / unfiltered fallback -------------------------------

        [Fact]
        public async Task A_tenant_metric_with_no_service_identity_fails_closed()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-noid", new[] { AggregateMode }, Seed);
            var table = (await harness.ModelAsync()).GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            // Enabled surface, credential configured, but NO service identity → excluded, NOT run
            // under an empty/anonymous context that would leak global data.
            var scope = Resolver(serviceIdentity: null).ResolveScope(config, table);

            scope.IsIncluded.Should().BeFalse();
            scope.UserContext.Should().BeNull();
            scope.Reason.Should().Contain("service identity");
        }

        [Fact]
        public async Task Per_tenant_mode_with_no_service_identity_fails_closed()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("scope-pt-noid", new[] { PerTenantMode }, Seed);
            var table = (await harness.ModelAsync()).GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var scope = Resolver(serviceIdentity: null).ResolveScope(config, table);

            scope.IsIncluded.Should().BeFalse();
        }
    }
}
