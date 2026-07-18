using System.Collections.Generic;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// One materialized group of a Prometheus business metric: the group's label
    /// dimension values plus the aggregated <c>COUNT(*)</c> and/or <c>SUM(column)</c>
    /// for the rows in that group.
    ///
    /// <para><see cref="Labels"/> are ordered by label NAME (a stable dimension order
    /// shared by every sample of the same series); the SAMPLES within a
    /// <see cref="PrometheusMetricSeries"/> are ordered by their label-VALUE tuple, so
    /// the exposition output is byte-stable across scrapes and dialects and does not
    /// churn.</para>
    ///
    /// <para><see cref="Count"/>/<see cref="Sum"/> are null only when the metric did
    /// not declare that source; a declared source over an empty/NULL column materializes
    /// as <c>0</c>, never null.</para>
    /// </summary>
    public sealed record PrometheusMetricSample(
        IReadOnlyList<KeyValuePair<string, string>> Labels,
        double? Count,
        double? Sum);

    /// <summary>
    /// A collected Prometheus metric: its exported (normalized) name, optional HELP text,
    /// and the deterministically sorted group samples produced by running the metric's
    /// grouped-aggregate intent through <see cref="Core.Resolvers.IQueryIntentExecutor"/>.
    /// </summary>
    public sealed record PrometheusMetricSeries(
        string MetricName,
        string? Help,
        IReadOnlyList<PrometheusMetricSample> Samples);
}
