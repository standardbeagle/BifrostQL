using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Test.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// The Prometheus aggregate planner + collector end to end over a real transformer-pipeline
    /// executor on seeded SQLite (reusing <see cref="ODataRealDbHarness"/>). Proves the aggregate
    /// crosses <see cref="Core.Resolvers.IQueryIntentExecutor"/> (never raw SQL), produces
    /// deterministically sorted COUNT/SUM series with zero-or-more labels, is tenant-SCOPED (not a
    /// global aggregate) because the security transformers still run, enforces the per-metric
    /// cardinality bound, and honors timeout/cancellation.
    /// </summary>
    public sealed class PrometheusSeriesCollectorTests
    {
        // region + product labels, an amount to sum, on a non-tenant table (everyone can read).
        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, product TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, product, amount) VALUES " +
                "(1, 'west', 'a', 10.0), (2, 'west', 'a', 5.0), (3, 'east', 'b', 2.0), " +
                "(4, 'east', 'a', 3.0), (5, 'west', 'b', 1.0);",
            // Tenant-scoped table: the collector must aggregate ONLY the caller's tenant rows.
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, status TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Orders(id, tenant_id, status, amount) VALUES " +
                "(1, 'tenant-a', 'open', 100.0), (2, 'tenant-a', 'open', 50.0), (3, 'tenant-a', 'closed', 25.0), " +
                "(4, 'tenant-b', 'open', 999.0), (5, 'tenant-b', 'closed', 888.0);",
        };

        private static readonly string[] Metadata =
        {
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount; metric-labels: region, product }",
            "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
                "metric-sum: amount; metric-labels: status; metric-security-mode: per-tenant; metric-max-cardinality: 100 }",
        };

        private static Dictionary<string, object?> TenantContext(string tenant) =>
            new() { ["tenant_id"] = tenant };

        private static async Task<(PrometheusSeriesCollector collector, IDbModel model)> SetupAsync(
            ODataRealDbHarness harness, PrometheusCollectionOptions? options = null)
        {
            var model = await harness.ModelAsync();
            return (new PrometheusSeriesCollector(harness.Reads, options), model);
        }

        // ---- criterion 2: COUNT + SUM with 2 labels → deterministic sorted series -----------

        [Fact]
        public async Task Count_and_sum_with_two_labels_produce_deterministic_sorted_series()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-2label", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var series = await collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            series.MetricName.Should().Be("sales_total");

            // Labels sort by NAME within a sample (product, region); samples sort by that
            // label-VALUE tuple: (a,east), (a,west), (b,east), (b,west).
            var shaped = series.Samples
                .Select(s => (
                    labels: s.Labels.Select(l => $"{l.Key}={l.Value}").ToArray(),
                    count: s.Count,
                    sum: s.Sum))
                .ToList();

            shaped.Select(s => string.Join(",", s.labels)).Should().Equal(
                "product=a,region=east",
                "product=a,region=west",
                "product=b,region=east",
                "product=b,region=west");

            shaped.Select(s => s.count).Should().Equal(1, 2, 1, 1);
            shaped.Select(s => s.sum).Should().Equal(3.0, 15.0, 2.0, 1.0);

            // Labels within a sample are ordered by NAME (product before region), stable.
            series.Samples[0].Labels.Select(l => l.Key).Should().Equal("product", "region");
        }

        [Fact]
        public async Task Series_order_is_stable_across_repeated_collections()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-stable", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            string Fingerprint(PrometheusMetricSeries s) => string.Join("|", s.Samples.Select(x =>
                string.Join(",", x.Labels.Select(l => $"{l.Key}={l.Value}")) + $":{x.Count}:{x.Sum}"));

            var a = await collector.CollectAsync(config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());
            var b = await collector.CollectAsync(config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            Fingerprint(a).Should().Be(Fingerprint(b));
        }

        // ---- criterion 2: ZERO labels → a single aggregate sample ---------------------------

        [Fact]
        public async Task Count_and_sum_with_zero_labels_produce_a_single_ungrouped_sample()
        {
            var meta = new[]
            {
                "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount }",
            };
            await using var harness = await ODataRealDbHarness.StartAsync("prom-0label", meta, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var series = await collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            series.Samples.Should().ContainSingle();
            var only = series.Samples[0];
            only.Labels.Should().BeEmpty();
            only.Count.Should().Be(5);
            only.Sum.Should().Be(21.0); // 10+5+2+3+1
        }

        // ---- criterion 3: tenant-scoped aggregate is SCOPED, not global ---------------------

        [Fact]
        public async Task Tenant_scoped_aggregate_counts_only_the_callers_tenant()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-tenant", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var series = await collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, TenantContext("tenant-a"));

            // tenant-a rows: open=2 (sum 150), closed=1 (sum 25). tenant-b's 999/888 MUST be absent.
            var byStatus = series.Samples.ToDictionary(
                s => s.Labels.Single(l => l.Key == "status").Value, s => (s.Count, s.Sum));

            byStatus.Keys.Should().BeEquivalentTo(new[] { "closed", "open" });
            byStatus["open"].Should().Be((2d, 150d));
            byStatus["closed"].Should().Be((1d, 25d));

            // The global total (which would include tenant-b) is 5 rows / 2062.0 — prove we are NOT that.
            series.Samples.Sum(s => s.Count!.Value).Should().Be(3);
            series.Samples.Sum(s => s.Sum!.Value).Should().Be(175d);
        }

        [Fact]
        public async Task Missing_tenant_context_fails_closed()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-noctx", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            // No tenant_id in context → the tenant transformer aborts the query (fail-closed).
            var act = () => collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            await act.Should().ThrowAsync<Exception>();
        }

        // ---- criterion 4: cardinality bound ------------------------------------------------

        [Fact]
        public async Task Exceeding_the_cardinality_bound_rejects_deterministically()
        {
            // status has 2 distinct values for tenant-a; bound of 1 must reject.
            var meta = new[]
            {
                "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
                    "metric-labels: status; metric-security-mode: per-tenant; metric-max-cardinality: 1 }",
            };
            await using var harness = await ODataRealDbHarness.StartAsync("prom-card", meta, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var act = () => collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, TenantContext("tenant-a"));

            (await act.Should().ThrowAsync<PrometheusCardinalityExceededException>())
                .Which.Observed.Should().Be(2);
        }

        [Fact]
        public async Task Within_the_cardinality_bound_collects_normally()
        {
            var meta = new[]
            {
                "main.Orders { tenant-filter: tenant_id; metric-name: orders_total; metric-count: enabled; " +
                    "metric-labels: status; metric-security-mode: per-tenant; metric-max-cardinality: 2 }",
            };
            await using var harness = await ODataRealDbHarness.StartAsync("prom-card-ok", meta, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Orders");
            var config = PrometheusMetricConfig.FromTable(table);

            var series = await collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, TenantContext("tenant-a"));

            series.Samples.Should().HaveCount(2);
        }

        // ---- criterion 4: timeout + cancellation -------------------------------------------

        [Fact]
        public async Task A_zero_timeout_fails_as_a_clean_collection_timeout()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-timeout", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness, new PrometheusCollectionOptions { QueryTimeout = TimeSpan.Zero });
            var table = model.GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            var act = () => collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>());

            await act.Should().ThrowAsync<PrometheusCollectionTimeoutException>();
        }

        [Fact]
        public async Task A_precancelled_token_propagates_as_operation_cancelled()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("prom-cancel", Metadata, Seed);
            var (collector, model) = await SetupAsync(harness);
            var table = model.GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = () => collector.CollectAsync(
                config, table, ODataRealDbHarness.EndpointPath, new Dictionary<string, object?>(), cts.Token);

            // Caller cancellation surfaces as OperationCanceledException, NOT a timeout.
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
