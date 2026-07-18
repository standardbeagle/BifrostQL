using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Prometheus;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Prometheus
{
    /// <summary>
    /// Criterion 2 — scrape-load behavior of the mounted <c>/metrics</c> endpoint under the shipped
    /// single-flight cache, cardinality guard, query timeout, and failure isolation:
    /// <list type="bullet">
    /// <item><b>single-flight</b> — N concurrent scrapes collapse to ONE aggregate query per TTL
    /// window (proven at the endpoint, counting the live queries the read pipeline actually ran).</item>
    /// <item><b>bounded series</b> — a metric exceeding its cardinality cap is capped deterministically,
    /// no unbounded series set on the wire.</item>
    /// <item><b>timeout</b> — a collection that exceeds the query timeout is cancelled cleanly and
    /// surfaced as a health metric, never a partial series or an internal message.</item>
    /// <item><b>recovery</b> — a failed collection surfaces a health metric and does NOT poison the
    /// surface: a subsequent scrape succeeds. (Injected at the read seam, since a transient DB failure
    /// is not reproducible from the HTTP wire alone.)</item>
    /// </list>
    /// </summary>
    public sealed class PrometheusScrapeLoadTests
    {
        private static readonly string[] SalesSeed =
        {
            "CREATE TABLE Sales (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount REAL NOT NULL);",
            "INSERT INTO Sales(id, region, amount) VALUES (1, 'west', 10.0), (2, 'east', 2.0), (3, 'west', 3.0);",
        };

        private static readonly string[] SalesNoLabels =
        {
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-sum: amount }",
        };

        private static readonly string[] SalesLabeled =
        {
            "main.Sales { metric-name: sales_total; metric-count: enabled; metric-labels: region }",
        };

        // ---- single-flight: N concurrent scrapes → ONE query per TTL window ----------------------

        [Fact]
        public async Task Concurrent_scrapes_collapse_to_one_aggregate_query_per_ttl_window()
        {
            var recorder = new RecordingQueryObserver();
            var release = new TaskCompletionSource();
            // Hold the first in-flight collection open so all sibling scrapes pile onto the same
            // single-flight slot instead of each issuing their own query.
            recorder.GateOn(() => release.Task);

            await using var host = await PrometheusScrapeHost.StartAsync(
                "load-sf", SalesNoLabels, SalesSeed, observers: new[] { recorder });

            var scrapes = Enumerable.Range(0, 8)
                .Select(_ => host.Client.SendAsync(host.Scrape()))
                .ToList();

            await Task.Delay(200); // let every scrape reach the single-flight cache
            release.SetResult();
            var responses = await Task.WhenAll(scrapes);

            foreach (var r in responses)
            {
                r.StatusCode.Should().Be(HttpStatusCode.OK);
                r.Dispose();
            }
            recorder.CountFor("Sales").Should().Be(1, "8 concurrent scrapes coalesce to one query");
        }

        // ---- bounded series: a metric over its cardinality cap is capped, not emitted ------------

        [Fact]
        public async Task A_metric_exceeding_its_cardinality_cap_is_capped_deterministically()
        {
            // region has 2 distinct values; a global backstop of 1 must cap it.
            await using var host = await PrometheusScrapeHost.StartAsync(
                "load-cap", SalesLabeled, SalesSeed,
                configure: p => p.Exposition = new PrometheusExpositionOptions
                {
                    Endpoint = PrometheusScrapeHost.EndpointPath,
                    GlobalMaxCardinality = 1,
                });

            var body = await host.ScrapeBodyAsync();

            body.Should().Contain("bifrostql_prometheus_metric_capped{metric=\"sales_total\"} 1\n");
            body.Should().NotContain("sales_total{region"); // the capped series is never emitted
        }

        // ---- timeout: a collection over the deadline is cancelled cleanly and surfaced ------------

        [Fact]
        public async Task A_collection_over_the_query_timeout_is_cancelled_and_surfaced_as_a_health_metric()
        {
            // A zero query-timeout cancels every collection immediately (deterministic timeout).
            await using var host = await PrometheusScrapeHost.StartAsync(
                "load-timeout", SalesNoLabels, SalesSeed,
                configure: p => p.Collection = new PrometheusCollectionOptions { QueryTimeout = TimeSpan.Zero });

            using var response = await host.Client.SendAsync(host.Scrape());
            var body = await response.Content.ReadAsStringAsync();

            // The whole scrape still returns 200 (a per-metric timeout never fails the scrape); the
            // metric surfaces as an error and emits NO business series or internal detail.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"sales_total\"} 1\n");
            body.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 0\n");
            body.Should().NotContain("sales_total ");
            body.Should().NotContain("Timeout");
        }

        // ---- recovery: a failed scrape does not poison the surface -------------------------------

        [Fact]
        public async Task A_failed_collection_surfaces_a_health_metric_then_a_later_scrape_recovers()
        {
            await using var host = await PrometheusScrapeHost.StartAsync("load-recover", SalesNoLabels, SalesSeed);

            // Build the scrape service directly over a fault-injecting read seam. A transient DB
            // failure is not reproducible from the HTTP wire, so the recovery property (no permanent
            // poisoning) is proven at the read seam the endpoint itself uses.
            var faulting = new FaultInjectingReads(host.Reads);
            var security = new PrometheusScrapeSecurityOptions
            {
                BusinessMetricsEnabled = true,
                ScrapeCredential = PrometheusScrapeHost.Credential,
            };
            var now = DateTimeOffset.UtcNow;
            var options = new PrometheusExpositionOptions
            {
                Endpoint = PrometheusScrapeHost.EndpointPath,
                CacheTtl = TimeSpan.FromSeconds(10),
            };
            var service = new PrometheusScrapeService(
                faulting,
                new PrometheusScrapeScopeResolver(security),
                new PrometheusSeriesCollector(faulting),
                options,
                clock: () => now);

            // Scrape 1 fails: the collection throws → health error metric, no business series.
            faulting.FailNext = true;
            var failed = await service.ScrapeAsync();
            failed.Should().Contain("bifrostql_prometheus_scrape_error{metric=\"sales_total\"} 1\n");
            failed.Should().NotContain("sales_total 3\n");

            // Advance past the TTL so the next scrape actually re-collects (a failure never cached as
            // fresh), then let it succeed: the surface recovered, no permanent poisoning.
            now += TimeSpan.FromSeconds(11);
            faulting.FailNext = false;
            var recovered = await service.ScrapeAsync();
            recovered.Should().Contain("bifrostql_prometheus_scrape_success{metric=\"sales_total\"} 1\n");
            recovered.Should().Contain("sales_total 3\n"); // all 3 rows counted
        }

        /// <summary>A read seam that throws once on demand, to prove a failed collection recovers.</summary>
        private sealed class FaultInjectingReads : IQueryIntentExecutor
        {
            private readonly IQueryIntentExecutor _inner;
            public FaultInjectingReads(IQueryIntentExecutor inner) => _inner = inner;

            public bool FailNext { get; set; }

            public Task<IDbModel> GetModelAsync(string? endpoint = null) => _inner.GetModelAsync(endpoint);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
            {
                if (FailNext)
                    throw new InvalidOperationException("transient db failure");
                return _inner.ExecuteAsync(intent, cancellationToken);
            }
        }
    }
}
