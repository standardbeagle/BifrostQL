using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Observers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Slice 5 criteria 1-3 for the engine self-metric REGISTRY: every instrument is present and
    /// thread-safe, the disabled path is a zero-work no-op, and the label domain is a finite enum by
    /// construction (a bounded-cardinality + no-info-leak invariant).
    /// </summary>
    public sealed class EngineMetricsTests
    {
        [Fact]
        public void All_required_instruments_present_in_snapshot()
        {
            var metrics = new EngineMetrics(enabled: true);

            metrics.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);
            metrics.RecordRequest(EngineOperation.Write, EngineRequestOutcome.Error);
            metrics.RecordSqlDuration(EngineOperation.Read, 0.02);
            metrics.RecordTransformerDuration(EngineOperation.Write, 0.003);
            metrics.ConnectionOpened(EngineAdapter.Grpc);

            var snap = metrics.Snapshot();

            // request outcome/count
            snap.Requests.Should().Contain(r =>
                r.Operation == EngineOperation.Read && r.Outcome == EngineRequestOutcome.Success && r.Count == 1);
            snap.Requests.Should().Contain(r =>
                r.Operation == EngineOperation.Write && r.Outcome == EngineRequestOutcome.Error && r.Count == 1);
            // SQL duration + transformer duration histograms (per operation)
            snap.SqlDurations.Should().Contain(h => h.Operation == EngineOperation.Read && h.Count == 1);
            snap.TransformerDurations.Should().Contain(h => h.Operation == EngineOperation.Write && h.Count == 1);
            // active connections
            snap.Connections.Should().Contain(c => c.Adapter == EngineAdapter.Grpc && c.Active == 1);
        }

        [Fact]
        public void Disabled_path_records_nothing_and_stays_zero()
        {
            var metrics = new EngineMetrics(enabled: false);

            metrics.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);
            metrics.RecordSqlDuration(EngineOperation.Read, 0.5);
            metrics.RecordTransformerDuration(EngineOperation.Read, 0.5);
            metrics.ConnectionOpened(EngineAdapter.GraphQL);

            var snap = metrics.Snapshot();

            snap.Requests.Should().OnlyContain(r => r.Count == 0);
            snap.SqlDurations.Should().OnlyContain(h => h.Count == 0);
            snap.TransformerDurations.Should().OnlyContain(h => h.Count == 0);
            snap.Connections.Should().OnlyContain(c => c.Active == 0);
        }

        [Fact]
        public void Connection_gauge_tracks_open_and_close()
        {
            var metrics = new EngineMetrics(enabled: true);

            metrics.ConnectionOpened(EngineAdapter.Pgwire);
            metrics.ConnectionOpened(EngineAdapter.Pgwire);
            metrics.ConnectionClosed(EngineAdapter.Pgwire);

            metrics.Snapshot().Connections
                .Single(c => c.Adapter == EngineAdapter.Pgwire).Active.Should().Be(1);
        }

        [Fact]
        public async Task Concurrent_increments_are_thread_safe()
        {
            var metrics = new EngineMetrics(enabled: true);
            const int workers = 8;
            const int perWorker = 5000;

            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perWorker; i++)
                {
                    metrics.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);
                    metrics.RecordSqlDuration(EngineOperation.Read, 0.02);
                    metrics.ConnectionOpened(EngineAdapter.Resp);
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            var snap = metrics.Snapshot();
            const long expected = workers * perWorker;
            snap.Requests.Single(r =>
                r.Operation == EngineOperation.Read && r.Outcome == EngineRequestOutcome.Success)
                .Count.Should().Be(expected);
            snap.SqlDurations.Single(h => h.Operation == EngineOperation.Read).Count.Should().Be(expected);
            snap.Connections.Single(c => c.Adapter == EngineAdapter.Resp).Active.Should().Be(expected);
        }

        [Fact]
        public void Histogram_buckets_are_cumulative_ascending_with_inf_last()
        {
            var metrics = new EngineMetrics(enabled: true);

            // one obs in the <=0.005 bucket, one in the <=0.1 bucket, one over the top boundary (+Inf)
            metrics.RecordSqlDuration(EngineOperation.Read, 0.004);
            metrics.RecordSqlDuration(EngineOperation.Read, 0.09);
            metrics.RecordSqlDuration(EngineOperation.Read, 42.0);

            var hist = metrics.Snapshot().SqlDurations.Single(h => h.Operation == EngineOperation.Read);

            hist.Count.Should().Be(3);
            hist.Sum.Should().BeApproximately(0.004 + 0.09 + 42.0, 1e-9);

            // buckets ascending, +Inf last, cumulative non-decreasing, top bucket == total count
            hist.Buckets.Select(b => b.UpperBound).Should()
                .BeEquivalentTo(EngineMetrics.DurationBucketsSeconds.Cast<double>()
                    .Concat(new[] { double.PositiveInfinity }), o => o.WithStrictOrdering());
            hist.Buckets.Select(b => b.CumulativeCount).Should().BeInAscendingOrder();
            hist.Buckets.Last().UpperBound.Should().Be(double.PositiveInfinity);
            hist.Buckets.Last().CumulativeCount.Should().Be(3);

            // exactly one at/under 0.005, two at/under 0.1
            hist.Buckets.Single(b => b.UpperBound == 0.005).CumulativeCount.Should().Be(1);
            hist.Buckets.Single(b => b.UpperBound == 0.1).CumulativeCount.Should().Be(2);
        }
    }
}
