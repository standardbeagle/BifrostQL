using System;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Thrown when a metric's grouped aggregate returns MORE distinct label groups than the
    /// metric may emit: its <c>metric-max-cardinality</c> bound, narrowed by the model's
    /// <c>max-query-rows</c> ceiling. The query fetches one group past that page, so
    /// <see cref="Observed"/> is a lower bound on the group count. The collection is rejected
    /// deterministically (fail-fast, no fallback) so a misconfigured high-cardinality label
    /// column can never emit an unbounded set of series onto the scrape wire.
    /// </summary>
    public sealed class PrometheusCardinalityExceededException : Exception
    {
        public PrometheusCardinalityExceededException(string? metricName, int limit, int observed)
            : base($"Prometheus metric '{metricName}' produced at least {observed} label groups, exceeding " +
                   $"its effective cardinality bound of {limit}; the metric is rejected rather than emitting " +
                   "an unbounded or partial series set.")
        {
            MetricName = metricName;
            Limit = limit;
            Observed = observed;
        }

        public string? MetricName { get; }
        public int Limit { get; }
        public int Observed { get; }
    }

    /// <summary>
    /// Thrown when a metric collection exceeds its <see cref="PrometheusCollectionOptions.QueryTimeout"/>.
    /// Distinguishes a timeout (the collector's own deadline) from a caller-initiated
    /// cancellation, which propagates as the original <see cref="OperationCanceledException"/>.
    /// </summary>
    public sealed class PrometheusCollectionTimeoutException : Exception
    {
        public PrometheusCollectionTimeoutException(string? metricName, TimeSpan timeout, Exception? inner = null)
            : base($"Prometheus metric '{metricName}' collection exceeded its {timeout.TotalMilliseconds:0}ms " +
                   "query timeout and was cancelled.", inner)
        {
            MetricName = metricName;
            Timeout = timeout;
        }

        public string? MetricName { get; }
        public TimeSpan Timeout { get; }
    }
}
