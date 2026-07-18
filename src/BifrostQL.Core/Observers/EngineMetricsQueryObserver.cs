using BifrostQL.Core.Modules;

namespace BifrostQL.Core.Observers
{
    /// <summary>
    /// Feeds the engine self-metric registry from the query (read) lifecycle: on
    /// <see cref="QueryPhase.AfterExecute"/> it records the request under its (read, success) counter
    /// and the SQL execution duration into the read histogram. The dimensions are the fixed
    /// <see cref="EngineOperation"/>/<see cref="EngineRequestOutcome"/> enums — never the table,
    /// tenant, user, or SQL text — so the emitted labels are bounded by construction (criterion 2).
    /// The write dimension is fed by <see cref="EngineMetricsMutationObserver"/>.
    ///
    /// <para>Recursion guard (criterion 3): the Prometheus scrape collects its business series by
    /// executing aggregate intents through the same query path this observer watches. If those
    /// scrape-internal queries were recorded, each scrape would generate engine metrics that the
    /// next scrape collects — an unbounded feedback loop. The scrape marks its intents' user context
    /// with <see cref="ScrapeInternalContextKey"/>; this observer skips any query carrying that
    /// marker, so serving a scrape never measures itself.</para>
    /// </summary>
    public sealed class EngineMetricsQueryObserver : IQueryObserver
    {
        /// <summary>
        /// User-context marker key set by the Prometheus scrape on its internal aggregate intents so
        /// this observer excludes them from self-instrumentation (no recursive measurement).
        /// </summary>
        public const string ScrapeInternalContextKey = "_bifrost_engine_metrics_scrape_internal";

        private readonly EngineMetrics _metrics;

        public EngineMetricsQueryObserver(EngineMetrics metrics)
        {
            _metrics = metrics;
        }

        // Only AfterExecute carries a completed outcome + SQL duration; the earlier phases contribute
        // nothing to these instruments, so we subscribe to just this one.
        public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

        public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
        {
            // Cheap disabled/guard checks first — no allocation on the hot path when off or when this
            // is the scrape observing its own collection query.
            if (!_metrics.Enabled || phase != QueryPhase.AfterExecute)
                return ValueTask.CompletedTask;
            if (context.UserContext.ContainsKey(ScrapeInternalContextKey))
                return ValueTask.CompletedTask;

            // The query-observer seam is the READ path (QueryType is a query shape, never a mutation);
            // writes are recorded by EngineMetricsMutationObserver. AfterExecute fires only on a
            // completed execution, so the outcome here is a success; error/denied outcomes are the
            // error path's to record (it does not reach this phase).
            _metrics.RecordRequest(EngineOperation.Read, EngineRequestOutcome.Success);

            if (context.Duration is { } duration)
                _metrics.RecordSqlDuration(EngineOperation.Read, duration.TotalSeconds);

            return ValueTask.CompletedTask;
        }
    }
}
