using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Collects one Prometheus business metric by planning its grouped-aggregate intent
    /// (<see cref="PrometheusMetricPlanner"/>) and executing it through the unskippable
    /// <see cref="IQueryIntentExecutor"/> pipeline — so the tenant/soft-delete/policy
    /// transformers scope the aggregate exactly as they scope every row read; the collector
    /// itself builds NO predicate. It maps the flat grouped result set into deterministically
    /// sorted <see cref="PrometheusMetricSample"/>s, enforces the per-metric cardinality
    /// bound, and applies a query timeout on top of caller cancellation.
    ///
    /// <para>NOT built here (later slices): the HTTP exposition endpoint (slice 4), the scrape
    /// auth gate / security-mode enforcement (slice 3), and any cache/self-metrics.</para>
    /// </summary>
    public sealed class PrometheusSeriesCollector
    {
        private readonly IQueryIntentExecutor _reads;
        private readonly PrometheusCollectionOptions _options;

        public PrometheusSeriesCollector(IQueryIntentExecutor reads, PrometheusCollectionOptions? options = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _options = options ?? new PrometheusCollectionOptions();
        }

        public async Task<PrometheusMetricSeries> CollectAsync(
            PrometheusMetricConfig config,
            IDbTable table,
            string? endpoint,
            IDictionary<string, object?> userContext,
            CancellationToken cancellationToken = default)
        {
            var plan = PrometheusMetricPlanner.Plan(config, table, endpoint, userContext);

            var result = await ExecuteWithDeadlineAsync(plan, cancellationToken);

            // Cardinality bound (maximum returned groups) — reject deterministically before
            // materializing any series so a misconfigured high-cardinality label can never
            // emit an unbounded series set. (A count over the flat result is O(groups), and
            // rejection short-circuits the sort/emit below entirely.)
            if (config.MaxCardinality is { } bound && result.Rows.Count > bound)
                throw new PrometheusCardinalityExceededException(config.MetricName, bound, result.Rows.Count);

            var samples = new List<PrometheusMetricSample>(result.Rows.Count);
            foreach (var row in result.Rows)
            {
                // Labels ordered by NAME → a stable dimension order shared by every sample.
                var labels = plan.Labels
                    .Select(b => new KeyValuePair<string, string>(b.LabelName, FormatLabelValue(Read(row, b.ResultKey))))
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToList();

                double? count = plan.IncludesCount ? ToDouble(Read(row, plan.CountResultKey)) : null;
                double? sum = plan.SumResultKey != null ? ToDouble(Read(row, plan.SumResultKey)) : null;

                samples.Add(new PrometheusMetricSample(labels, count, sum));
            }

            // Samples ordered by label-VALUE tuple → byte-stable scrape output across dialects.
            samples.Sort(CompareByLabelValues);

            return new PrometheusMetricSeries(config.ExportedName!, config.Help, samples);
        }

        private async Task<QueryIntentResult> ExecuteWithDeadlineAsync(
            PrometheusMetricPlan plan, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.QueryTimeout > TimeSpan.Zero)
                timeoutCts.CancelAfter(_options.QueryTimeout);
            else if (_options.QueryTimeout == TimeSpan.Zero)
                timeoutCts.Cancel();

            try
            {
                return await _reads.ExecuteAsync(plan.Intent, timeoutCts.Token);
            }
            catch (OperationCanceledException ex)
                when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Our deadline fired, not the caller's token → a timeout, distinctly typed.
                throw new PrometheusCollectionTimeoutException(plan.Config.MetricName, _options.QueryTimeout, ex);
            }
            // A caller-initiated cancellation propagates as the original OperationCanceledException.
        }

        private static object? Read(IReadOnlyDictionary<string, object?> row, string key) =>
            row.TryGetValue(key, out var value) ? value : null;

        private static string FormatLabelValue(object? value) =>
            value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        // A declared-but-NULL aggregate (e.g. SUM over all-NULL rows) materializes as 0, never null.
        private static double ToDouble(object? value) =>
            value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);

        private static int CompareByLabelValues(PrometheusMetricSample a, PrometheusMetricSample b)
        {
            // Both samples of one series share identical label names in identical (name-sorted)
            // order, so a pairwise value compare is a well-defined total order.
            var count = Math.Min(a.Labels.Count, b.Labels.Count);
            for (var i = 0; i < count; i++)
            {
                var cmp = string.CompareOrdinal(a.Labels[i].Value, b.Labels[i].Value);
                if (cmp != 0) return cmp;
            }
            return a.Labels.Count.CompareTo(b.Labels.Count);
        }
    }
}
