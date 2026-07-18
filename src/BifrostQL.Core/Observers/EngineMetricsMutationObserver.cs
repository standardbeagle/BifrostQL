using BifrostQL.Core.Modules;

namespace BifrostQL.Core.Observers
{
    /// <summary>
    /// Feeds the WRITE dimension of the engine request counter from the mutation lifecycle: a
    /// completed mutation records one (write, success) request. The dimensions are the fixed
    /// <see cref="EngineOperation"/>/<see cref="EngineRequestOutcome"/> enums only — never the table,
    /// tenant, user, or mutation data — so the label domain stays bounded (criterion 2).
    ///
    /// <para>Mutations never run on the Prometheus scrape path (a scrape only reads), so no recursion
    /// guard is needed here. The read dimension + SQL duration are fed by
    /// <see cref="EngineMetricsQueryObserver"/>.</para>
    /// </summary>
    public sealed class EngineMetricsMutationObserver : IMutationObserver
    {
        private readonly EngineMetrics _metrics;

        public EngineMetricsMutationObserver(EngineMetrics metrics)
        {
            _metrics = metrics;
        }

        public ValueTask OnMutationAsync(MutationObserverContext context)
        {
            // Disabled-path guard first — no work when self-metrics are off.
            if (!_metrics.Enabled)
                return ValueTask.CompletedTask;

            // The post-commit observer fires only after a completed write → a success outcome.
            _metrics.RecordRequest(EngineOperation.Write, EngineRequestOutcome.Success);
            return ValueTask.CompletedTask;
        }
    }
}
