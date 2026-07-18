using System;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Bounds on a single Prometheus metric collection. <see cref="QueryTimeout"/> caps how
    /// long the grouped-aggregate query may run before the collection is cancelled cleanly;
    /// the per-metric cardinality bound (maximum returned groups) comes from the metric's own
    /// slice-1 <c>metric-max-cardinality</c> contract, not from here.
    /// </summary>
    public sealed class PrometheusCollectionOptions
    {
        /// <summary>
        /// Wall-clock cap on the aggregate query. A collection that exceeds it is cancelled
        /// and surfaces a <see cref="PrometheusCollectionTimeoutException"/> — never a partial
        /// or silently-dropped series. A non-positive value disables the timeout (caller-supplied
        /// cancellation still applies).
        /// </summary>
        public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(10);
    }
}
