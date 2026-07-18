using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Observers;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Prometheus
{
    /// <summary>
    /// Slice 5 criteria 2-3 for the engine-metrics OBSERVERS: the read observer records
    /// (read, success) + SQL duration on AfterExecute, the write observer records (write, success),
    /// BOTH carry only bounded enum labels (never the table/tenant/user in the context), the disabled
    /// path is a no-op, and — the crux — a scrape-internal query is EXCLUDED so serving a scrape can
    /// never measure its own collection queries (no recursive measurement).
    /// </summary>
    public sealed class EngineMetricsQueryObserverTests
    {
        private static QueryObserverContext Context(
            IDictionary<string, object?> userContext, TimeSpan? duration = null, string table = "secret_tenant_table")
        {
            var dbTable = Substitute.For<IDbTable>();
            dbTable.DbName.Returns(table);
            return new QueryObserverContext
            {
                Table = dbTable,
                Model = Substitute.For<IDbModel>(),
                UserContext = userContext,
                QueryType = QueryType.Standard,
                Path = "query.path",
                Duration = duration,
            };
        }

        [Fact]
        public async Task Read_observer_records_success_and_sql_duration()
        {
            var metrics = new EngineMetrics(enabled: true);
            var observer = new EngineMetricsQueryObserver(metrics);

            await observer.OnQueryPhaseAsync(
                QueryPhase.AfterExecute, Context(new Dictionary<string, object?>(), TimeSpan.FromMilliseconds(20)));

            var snap = metrics.Snapshot();
            snap.Requests.Single(r => r.Operation == EngineOperation.Read && r.Outcome == EngineRequestOutcome.Success)
                .Count.Should().Be(1);
            snap.SqlDurations.Single(h => h.Operation == EngineOperation.Read).Count.Should().Be(1);
        }

        [Fact]
        public async Task Scrape_internal_query_is_excluded_no_recursive_measurement()
        {
            var metrics = new EngineMetrics(enabled: true);
            var observer = new EngineMetricsQueryObserver(metrics);

            var scrapeCtx = new Dictionary<string, object?>
            {
                [EngineMetricsQueryObserver.ScrapeInternalContextKey] = true,
            };

            await observer.OnQueryPhaseAsync(
                QueryPhase.AfterExecute, Context(scrapeCtx, TimeSpan.FromMilliseconds(5)));

            var snap = metrics.Snapshot();
            snap.Requests.Should().OnlyContain(r => r.Count == 0);
            snap.SqlDurations.Should().OnlyContain(h => h.Count == 0);
        }

        [Fact]
        public async Task Disabled_observer_is_a_noop()
        {
            var metrics = new EngineMetrics(enabled: false);
            var observer = new EngineMetricsQueryObserver(metrics);

            await observer.OnQueryPhaseAsync(
                QueryPhase.AfterExecute, Context(new Dictionary<string, object?>(), TimeSpan.FromSeconds(1)));

            metrics.Snapshot().Requests.Should().OnlyContain(r => r.Count == 0);
        }

        [Fact]
        public async Task Write_observer_records_write_success()
        {
            var metrics = new EngineMetrics(enabled: true);
            var observer = new EngineMetricsMutationObserver(metrics);

            var ctx = new MutationObserverContext
            {
                Table = Substitute.For<IDbTable>(),
                MutationType = MutationType.Insert,
                Data = new Dictionary<string, object?>(),
                Result = null,
                UserContext = new Dictionary<string, object?>(),
                MutationState = MutationObserverContext.NewMutationState(),
            };

            await observer.OnMutationAsync(ctx);

            metrics.Snapshot().Requests
                .Single(r => r.Operation == EngineOperation.Write && r.Outcome == EngineRequestOutcome.Success)
                .Count.Should().Be(1);
        }
    }
}
