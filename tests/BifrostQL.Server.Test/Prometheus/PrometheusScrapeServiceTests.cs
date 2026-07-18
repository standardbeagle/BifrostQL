using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Observers;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Prometheus;
using BifrostQL.Server.Test.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// The exposition service end to end over the real slice-2/3 pipeline on seeded SQLite: it
    /// renders 0.0.4 text with health self-metrics (criteria 1/3), single-flights concurrent scrapes
    /// to one query per series (criterion 2), partitions the cache key by identity (criterion 2),
    /// never caches a failure as fresh while surfacing it via a health metric + explicit stale
    /// (criterion 3), and rejects/caps cardinality deterministically (criterion 4).
    /// </summary>
    public sealed class PrometheusScrapeServiceTests
    {
        private static readonly string[] Seed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, product TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, product, amount) VALUES " +
                "(1, 'west', 'a', 10.0), (2, 'west', 'a', 5.0), (3, 'east', 'b', 2.0), " +
                "(4, 'east', 'a', 3.0), (5, 'west', 'b', 1.0);",
        };

        private const string SalesMetric =
            "main.Sales { metric-name: sales_total; metric-help: Sales rows; metric-count: enabled; " +
            "metric-sum: amount; metric-labels: region }";
        private const string SalesNoLabels =
            "main.Sales { metric-name: sales_total; metric-help: Sales rows; metric-count: enabled; metric-sum: amount }";

        private static PrometheusScrapeService Service(
            IQueryIntentExecutor reads, PrometheusExpositionOptions options, Func<DateTimeOffset>? clock = null)
        {
            var security = new PrometheusScrapeSecurityOptions { BusinessMetricsEnabled = true, ScrapeCredential = "t" };
            return new PrometheusScrapeService(
                reads,
                new PrometheusScrapeScopeResolver(security),
                new PrometheusSeriesCollector(reads),
                options,
                clock);
        }

        private static PrometheusExpositionOptions Opts(
            int? global = 1000, TimeSpan? ttl = null) =>
            new()
            {
                Endpoint = ODataRealDbHarness.EndpointPath,
                GlobalMaxCardinality = global,
                CacheTtl = ttl ?? TimeSpan.FromSeconds(10),
            };

        // ---- criterion 1/3: valid 0.0.4 text with business + health metrics ----------------

        [Fact]
        public async Task Renders_business_series_and_health_metrics()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-basic", new[] { SalesMetric }, Seed);
            var text = await Service(harness.Reads, Opts()).ScrapeAsync();

            text.Should().Contain("# HELP sales_total Sales rows\n");
            text.Should().Contain("# TYPE sales_total gauge\n");
            text.Should().Contain("sales_total{region=\"east\"} 2\n"); // east: rows 3,4
            text.Should().Contain("sales_total{region=\"west\"} 3\n"); // west: rows 1,2,5
            text.Should().Contain("# TYPE sales_total_sum gauge\n");
            text.Should().Contain("sales_total_sum{region=\"east\"} 5\n");  // 2+3
            text.Should().Contain("sales_total_sum{region=\"west\"} 16\n"); // 10+5+1
            // Health: the metric collected successfully this scrape.
            text.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 1\n");
            text.Should().Contain("bifrostql_prometheus_last_success_timestamp_seconds{metric=\"sales_total\"}");
        }

        // ---- criterion 2: single-flight — N concurrent scrapes → ONE query per series -------

        [Fact]
        public async Task Concurrent_scrapes_collapse_to_one_query_per_series()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-sf", new[] { SalesNoLabels }, Seed);
            var gate = new TaskCompletionSource();
            var reads = new ControllableReads(harness.Reads) { Gate = () => gate.Task };
            var service = Service(reads, Opts());

            // Fire N scrapes; they all miss the cold cache and coalesce onto one in-flight collection.
            var scrapes = Enumerable.Range(0, 8).Select(_ => service.ScrapeAsync()).ToList();
            await Task.Delay(100); // let every scrape reach the single-flight cache
            gate.SetResult();
            await Task.WhenAll(scrapes);

            reads.ExecuteCount.Should().Be(1); // one aggregate query for the one series, not 8
        }

        // ---- criterion 2 (security): the cache key partitions by identity ------------------

        [Fact]
        public async Task Cache_key_partitions_by_identity_partition()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-key", new[] { SalesMetric }, Seed);
            var service = Service(harness.Reads, Opts());
            var table = (await harness.ModelAsync()).GetTableFromDbName("Sales");
            var config = PrometheusMetricConfig.FromTable(table);

            IDictionary<string, object?> A = new Dictionary<string, object?> { ["tenant_id"] = "tenant-a" };
            IDictionary<string, object?> B = new Dictionary<string, object?> { ["tenant_id"] = "tenant-b" };

            var keyA = service.CacheKey("model", config, A);
            var keyB = service.CacheKey("model", config, B);
            var keyA2 = service.CacheKey("model", config, new Dictionary<string, object?> { ["tenant_id"] = "tenant-a" });
            var keyEmpty = service.CacheKey("model", config, new Dictionary<string, object?>());

            keyA.Should().NotBe(keyB);     // identity A's slot is never identity B's slot (no cross-tenant leak)
            keyA.Should().Be(keyA2);       // same identity → same slot (coalescing still works)
            keyEmpty.Should().NotBe(keyA); // an empty/non-tenant partition is its own slot
        }

        // ---- criterion 3: a failure never caches partial-as-fresh + a health metric surfaces it

        [Fact]
        public async Task A_collection_failure_surfaces_a_health_metric_and_no_business_series()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-fail", new[] { SalesNoLabels }, Seed);
            var reads = new ControllableReads(harness.Reads) { FailWith = _ => new InvalidOperationException("db down") };
            var text = await Service(reads, Opts()).ScrapeAsync();

            text.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"sales_total\"} 1\n");
            text.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 0\n");
            text.Should().NotContain("sales_total{"); // no business series emitted
            text.Should().NotContain("db down");        // internal detail never on the wire
        }

        [Fact]
        public async Task A_later_failure_serves_the_last_success_as_explicit_stale()
        {
            var now = DateTimeOffset.UtcNow;
            await using var harness = await ODataRealDbHarness.StartAsync("svc-stale", new[] { SalesNoLabels }, Seed);
            var reads = new ControllableReads(harness.Reads);
            var service = Service(reads, Opts(ttl: TimeSpan.FromSeconds(10)), () => now);

            // First scrape succeeds and caches a fresh value.
            (await service.ScrapeAsync()).Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 1\n");

            // Expire the cache, then fail the next collection: the prior success is served STALE.
            now += TimeSpan.FromSeconds(11);
            reads.FailWith = n => n > 1 ? new InvalidOperationException("db down") : null;
            var text = await service.ScrapeAsync();

            text.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"sales_total\"} 1\n");
            text.Should().Contain("bifrostql_prometheus_metric_stale{metric=\"sales_total\"} 1\n");
            text.Should().Contain("sales_total 5\n"); // the stale count (5 rows) is still served
        }

        // ---- criterion 4: cardinality cap + unbounded-label rejection -----------------------

        [Fact]
        public async Task Exceeding_the_global_cardinality_cap_caps_deterministically_with_a_warning_metric()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-cap", new[] { SalesMetric }, Seed);
            // region has 2 distinct values; a global cap of 1 must cap it (no per-metric override).
            var text = await Service(harness.Reads, Opts(global: 1)).ScrapeAsync();

            text.Should().Contain("bifrostql_prometheus_metric_capped{metric=\"sales_total\"} 1\n");
            text.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 0\n");
            text.Should().NotContain("sales_total{region"); // capped series not emitted
        }

        [Fact]
        public async Task A_labeled_metric_with_no_cap_and_no_global_backstop_is_rejected()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-unbounded", new[] { SalesMetric }, Seed);
            // Global backstop disabled + no per-metric cap + labels present = unbounded config.
            var text = await Service(harness.Reads, Opts(global: null)).ScrapeAsync();

            text.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"sales_total\"} 1\n");
            text.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 0\n");
            text.Should().NotContain("sales_total{region"); // unbounded metric never emitted
        }

        // ---- slice 5: engine self-metrics rendered SEPARATELY from the business series ------

        private static PrometheusScrapeService ServiceWithEngine(
            IQueryIntentExecutor reads, PrometheusExpositionOptions options, EngineMetrics engine)
        {
            var security = new PrometheusScrapeSecurityOptions { BusinessMetricsEnabled = true, ScrapeCredential = "t" };
            return new PrometheusScrapeService(
                reads,
                new PrometheusScrapeScopeResolver(security),
                new PrometheusSeriesCollector(reads),
                options,
                clock: null,
                logger: null,
                engineMetrics: engine);
        }

        [Fact]
        public async Task Engine_self_metrics_appended_separately_when_enabled()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-engine-on", new[] { SalesMetric }, Seed);
            var engine = new EngineMetrics(enabled: true);
            engine.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);
            engine.RecordSqlDuration(EngineOperation.Read, 0.02);

            var text = await ServiceWithEngine(harness.Reads, Opts(), engine).ScrapeAsync();

            // business series still present …
            text.Should().Contain("sales_total{region=\"west\"} 3\n");
            // … and the engine self-metrics are appended in their OWN namespace.
            text.Should().Contain("# TYPE bifrostql_engine_requests_total counter");
            text.Should().Contain("bifrostql_engine_requests_total{operation=\"read\",outcome=\"success\"} 1");
            text.Should().Contain("# TYPE bifrostql_engine_sql_duration_seconds histogram");
        }

        [Fact]
        public async Task Engine_self_metrics_absent_when_disabled()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-engine-off", new[] { SalesMetric }, Seed);
            var engine = new EngineMetrics(enabled: false);
            engine.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);

            var text = await ServiceWithEngine(harness.Reads, Opts(), engine).ScrapeAsync();

            text.Should().Contain("sales_total{region=\"west\"} 3\n");
            text.Should().NotContain("bifrostql_engine_");
        }

        [Fact]
        public async Task Scrape_marks_collector_context_internal_so_no_recursive_measurement()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("svc-engine-guard", new[] { SalesMetric }, Seed);
            var spy = new ContextCapturingReads(harness.Reads);
            var engine = new EngineMetrics(enabled: true);

            await ServiceWithEngine(spy, Opts(), engine).ScrapeAsync();

            // The scrape's own aggregate intent carries the scrape-internal marker, so the engine
            // observer excludes it — a scrape can never measure its own collection queries.
            spy.CapturedContexts.Should().NotBeEmpty();
            spy.CapturedContexts.Should().OnlyContain(
                ctx => ctx.ContainsKey(EngineMetricsQueryObserver.ScrapeInternalContextKey));
        }

        /// <summary>Captures the user context of every executed intent, to assert the scrape marks its
        /// own collection queries scrape-internal (recursion guard).</summary>
        private sealed class ContextCapturingReads : IQueryIntentExecutor
        {
            private readonly IQueryIntentExecutor _inner;
            public List<IDictionary<string, object?>> CapturedContexts { get; } = new();

            public ContextCapturingReads(IQueryIntentExecutor inner) => _inner = inner;

            public Task<IDbModel> GetModelAsync(string? endpoint = null) => _inner.GetModelAsync(endpoint);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
            {
                CapturedContexts.Add(intent.UserContext);
                return _inner.ExecuteAsync(intent, cancellationToken);
            }
        }

        /// <summary>An <see cref="IQueryIntentExecutor"/> decorator that counts executions, can hold
        /// them behind a gate (to force concurrent coalescing), and can inject failures per call.</summary>
        private sealed class ControllableReads : IQueryIntentExecutor
        {
            private readonly IQueryIntentExecutor _inner;
            private int _executeCount;

            public ControllableReads(IQueryIntentExecutor inner) => _inner = inner;

            public int ExecuteCount => _executeCount;
            public Func<Task>? Gate { get; set; }
            public Func<int, Exception?>? FailWith { get; set; }

            public Task<IDbModel> GetModelAsync(string? endpoint = null) => _inner.GetModelAsync(endpoint);

            public async Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
            {
                var n = Interlocked.Increment(ref _executeCount);
                if (Gate is not null)
                    await Gate();
                if (FailWith?.Invoke(n) is { } ex)
                    throw ex;
                return await _inner.ExecuteAsync(intent, cancellationToken);
            }
        }
    }
}
