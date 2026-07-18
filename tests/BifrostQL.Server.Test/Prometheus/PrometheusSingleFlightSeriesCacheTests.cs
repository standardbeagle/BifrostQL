using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Server.Prometheus;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Criteria 2/3 at the cache seam: single-flight coalescing (N concurrent cold callers → ONE
    /// factory run), TTL freshness (a hit within TTL never re-runs the factory), a failed collection
    /// never caches as fresh, and the last success survives a later failure for stale serving.
    /// </summary>
    public sealed class PrometheusSingleFlightSeriesCacheTests
    {
        private static PrometheusMetricSeries Series(string name) =>
            new(name, null, Array.Empty<PrometheusMetricSample>());

        [Fact]
        public async Task Concurrent_cold_callers_share_one_factory_run()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new PrometheusSingleFlightSeriesCache(TimeSpan.FromSeconds(10), () => now);
            var runs = 0;
            var gate = new TaskCompletionSource();

            async Task<PrometheusMetricSeries> Factory(CancellationToken _)
            {
                Interlocked.Increment(ref runs);
                await gate.Task; // hold every caller inside the single in-flight computation
                return Series("m");
            }

            // Fire N callers before releasing the gate so they all coalesce onto one in-flight task.
            var callers = Enumerable.Range(0, 8)
                .Select(_ => cache.GetOrCollectAsync("k", Factory, CancellationToken.None))
                .ToList();
            gate.SetResult();
            await Task.WhenAll(callers);

            runs.Should().Be(1);
        }

        [Fact]
        public async Task A_hit_within_ttl_does_not_rerun_the_factory()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new PrometheusSingleFlightSeriesCache(TimeSpan.FromSeconds(10), () => now);
            var runs = 0;

            Task<PrometheusMetricSeries> Factory(CancellationToken _)
            {
                runs++;
                return Task.FromResult(Series("m"));
            }

            await cache.GetOrCollectAsync("k", Factory, CancellationToken.None);
            now += TimeSpan.FromSeconds(5); // still within the 10s TTL
            await cache.GetOrCollectAsync("k", Factory, CancellationToken.None);

            runs.Should().Be(1);
        }

        [Fact]
        public async Task Past_ttl_the_factory_runs_again()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new PrometheusSingleFlightSeriesCache(TimeSpan.FromSeconds(10), () => now);
            var runs = 0;

            Task<PrometheusMetricSeries> Factory(CancellationToken _)
            {
                runs++;
                return Task.FromResult(Series("m"));
            }

            await cache.GetOrCollectAsync("k", Factory, CancellationToken.None);
            now += TimeSpan.FromSeconds(11); // past the TTL
            await cache.GetOrCollectAsync("k", Factory, CancellationToken.None);

            runs.Should().Be(2);
        }

        [Fact]
        public async Task A_failure_is_not_cached_as_fresh()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new PrometheusSingleFlightSeriesCache(TimeSpan.FromSeconds(10), () => now);

            Func<Task> failing = () => cache.GetOrCollectAsync(
                "k", _ => Task.FromException<PrometheusMetricSeries>(new InvalidOperationException("boom")),
                CancellationToken.None);

            await failing.Should().ThrowAsync<InvalidOperationException>();

            // No stale value exists (never succeeded), and a subsequent success now populates cleanly.
            cache.TryGetStale("k", out _).Should().BeFalse();
            var ok = await cache.GetOrCollectAsync("k", _ => Task.FromResult(Series("m")), CancellationToken.None);
            ok.MetricName.Should().Be("m");
        }

        [Fact]
        public async Task Last_success_survives_a_later_failure_as_stale()
        {
            var now = DateTimeOffset.UtcNow;
            var cache = new PrometheusSingleFlightSeriesCache(TimeSpan.FromSeconds(10), () => now);

            await cache.GetOrCollectAsync("k", _ => Task.FromResult(Series("good")), CancellationToken.None);
            now += TimeSpan.FromSeconds(11); // expire so the next call actually re-collects

            Func<Task> failing = () => cache.GetOrCollectAsync(
                "k", _ => Task.FromException<PrometheusMetricSeries>(new InvalidOperationException("boom")),
                CancellationToken.None);
            await failing.Should().ThrowAsync<InvalidOperationException>();

            cache.TryGetStale("k", out var stale).Should().BeTrue();
            stale.MetricName.Should().Be("good");
        }
    }
}
