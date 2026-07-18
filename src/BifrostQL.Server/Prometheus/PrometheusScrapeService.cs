using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Renders one Prometheus scrape: enumerates every metric-declaring table in the endpoint's
    /// cached model, resolves each metric's scope (slice-3), collects its series through the
    /// single-flight TTL cache over the slice-2 collector, applies the cardinality guard, and emits
    /// health self-metrics — then renders the whole set as 0.0.4 exposition text.
    ///
    /// <para>Assumes the credential gate has already accepted the scrape (the endpoint runs
    /// <see cref="PrometheusScrapeGate"/> first); this service performs the model lookup and
    /// collection that the gate protects. It builds NO predicate — every aggregate crosses
    /// <see cref="IQueryIntentExecutor"/> under the resolved scope's identity, so tenant/soft-delete/
    /// policy transformers scope it. Excluded scopes are dropped and logged server-side, never
    /// emitted unscoped. A per-metric collection failure is sanitized (logged server-side only,
    /// surfaced as a health metric — never verbatim on the wire) and never fails the whole scrape.</para>
    /// </summary>
    public sealed class PrometheusScrapeService
    {
        // Self / health metric names (fixed, Prometheus-valid) that surface scrape posture to the
        // operator regardless of business-series success.
        internal const string ScrapeSuccessMetric = "bifrostql_prometheus_scrape_success";
        internal const string ScrapeErrorMetric = "bifrostql_prometheus_scrape_error";
        internal const string MetricCappedMetric = "bifrostql_prometheus_metric_capped";
        internal const string MetricStaleMetric = "bifrostql_prometheus_metric_stale";
        internal const string LastSuccessMetric = "bifrostql_prometheus_last_success_timestamp_seconds";

        private readonly IQueryIntentExecutor _reads;
        private readonly PrometheusScrapeScopeResolver _scopeResolver;
        private readonly PrometheusSeriesCollector _collector;
        private readonly PrometheusExpositionOptions _options;
        private readonly PrometheusSingleFlightSeriesCache _cache;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ILogger<PrometheusScrapeService>? _logger;

        public PrometheusScrapeService(
            IQueryIntentExecutor reads,
            PrometheusScrapeScopeResolver scopeResolver,
            PrometheusSeriesCollector collector,
            PrometheusExpositionOptions options,
            Func<DateTimeOffset>? clock = null,
            ILogger<PrometheusScrapeService>? logger = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _logger = logger;
            _cache = new PrometheusSingleFlightSeriesCache(_options.CacheTtl, _clock);
        }

        /// <summary>Collects all metrics and renders the 0.0.4 exposition body for one scrape.</summary>
        public async Task<string> ScrapeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var model = await _reads.GetModelAsync(_options.Endpoint);
            var modelToken = RuntimeHelpers.GetHashCode(model).ToString(CultureInfo.InvariantCulture);

            var businessFamilies = new List<PrometheusExpositionFamily>();
            var success = new List<PrometheusExpositionSample>();
            var errors = new List<PrometheusExpositionSample>();
            var capped = new List<PrometheusExpositionSample>();
            var stale = new List<PrometheusExpositionSample>();
            var lastSuccess = new List<PrometheusExpositionSample>();

            // Deterministic metric order by exported name; the writer re-sorts too, but a stable
            // enumeration keeps health-sample order stable as well.
            var metrics = model.Tables
                .Select(t => (table: t, config: PrometheusMetricConfig.FromTable(t)))
                .Where(x => x.config.DeclaresMetric)
                .OrderBy(x => x.config.ExportedName, StringComparer.Ordinal)
                .ToList();

            foreach (var (table, config) in metrics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metricLabel = MetricLabels(config.ExportedName!);

                var scope = _scopeResolver.ResolveScope(config, table);
                if (!scope.IsIncluded)
                {
                    // A misconfiguration/opt-out fails closed to NO metric; the reason is server-side
                    // only and never reaches the scrape wire.
                    _logger?.LogInformation(
                        "Prometheus metric '{Metric}' excluded from scrape: {Reason}",
                        config.ExportedName, scope.Reason);
                    continue;
                }

                var effectiveCap = config.MaxCardinality ?? _options.GlobalMaxCardinality;
                if (config.Labels.Count > 0 && effectiveCap is null)
                {
                    // A labeled metric with neither a per-metric cap nor a global backstop is an
                    // unbounded configuration — reject it (operator-visible error metric), never emit
                    // a potentially unbounded series set.
                    _logger?.LogWarning(
                        "Prometheus metric '{Metric}' declares labels but has no cardinality cap and the global " +
                        "backstop is disabled; rejecting as an unbounded configuration.", config.ExportedName);
                    errors.Add(new PrometheusExpositionSample(metricLabel, 1));
                    success.Add(new PrometheusExpositionSample(metricLabel, 0));
                    continue;
                }

                var key = CacheKey(modelToken, config, scope.UserContext);
                try
                {
                    var series = await _cache.GetOrCollectAsync(
                        key,
                        ct => _collector.CollectAsync(config, table, _options.Endpoint, scope.UserContext!, ct, effectiveCap),
                        cancellationToken);

                    businessFamilies.AddRange(ToFamilies(series));
                    success.Add(new PrometheusExpositionSample(metricLabel, 1));
                    lastSuccess.Add(new PrometheusExpositionSample(metricLabel, UnixSeconds(_clock())));
                }
                catch (PrometheusCardinalityExceededException ex)
                {
                    // Deterministic rejection: the metric is capped, not emitted, and the operator
                    // sees a dedicated capped self-metric.
                    _logger?.LogWarning(
                        "Prometheus metric '{Metric}' exceeded its cardinality cap ({Observed} > {Limit}); capped.",
                        config.ExportedName, ex.Observed, ex.Limit);
                    capped.Add(new PrometheusExpositionSample(metricLabel, 1));
                    success.Add(new PrometheusExpositionSample(metricLabel, 0));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // the whole scrape was cancelled — tear down cleanly.
                }
                catch (Exception ex)
                {
                    // Sanitize: full detail logged server-side ONLY, never forwarded onto the scrape
                    // wire (invariants 1/3). The failure is surfaced as a scrape-error self-metric.
                    _logger?.LogError(ex,
                        "Prometheus metric '{Metric}' collection failed.", config.ExportedName);
                    errors.Add(new PrometheusExpositionSample(metricLabel, 1));
                    success.Add(new PrometheusExpositionSample(metricLabel, 0));

                    // Explicit stale-serving: if a prior successful collection is still cached, serve
                    // it (the failure never overwrote it as fresh) and flag it stale so the operator
                    // knows the value is not from this scrape.
                    if (_cache.TryGetStale(key, out var staleSeries))
                    {
                        businessFamilies.AddRange(ToFamilies(staleSeries));
                        stale.Add(new PrometheusExpositionSample(metricLabel, 1));
                    }
                }
            }

            var families = new List<PrometheusExpositionFamily>(businessFamilies);
            AddHealthFamily(families, ScrapeSuccessMetric,
                "1 if the metric's series was collected on this scrape, else 0.", success);
            AddHealthFamily(families, ScrapeErrorMetric,
                "1 if the metric's collection failed on this scrape.", errors);
            AddHealthFamily(families, MetricCappedMetric,
                "1 if the metric was rejected on this scrape for exceeding its cardinality cap.", capped);
            AddHealthFamily(families, MetricStaleMetric,
                "1 if a cached (stale) series was served because this scrape's collection failed.", stale);
            AddHealthFamily(families, LastSuccessMetric,
                "Unix timestamp (seconds) of the metric's most recent successful collection.", lastSuccess);

            return PrometheusExpositionWriter.Write(families);
        }

        // A series carries COUNT and/or SUM per sample; emit the count under the metric name and the
        // sum under a `_sum`-suffixed family so two distinct value projections never collide.
        private static IEnumerable<PrometheusExpositionFamily> ToFamilies(PrometheusMetricSeries series)
        {
            if (series.Samples.Any(s => s.Count.HasValue))
                yield return new PrometheusExpositionFamily(
                    series.MetricName, series.Help, PrometheusExpositionWriter.GaugeType,
                    series.Samples.Where(s => s.Count.HasValue)
                        .Select(s => new PrometheusExpositionSample(s.Labels, s.Count!.Value)).ToList());

            if (series.Samples.Any(s => s.Sum.HasValue))
                yield return new PrometheusExpositionFamily(
                    series.MetricName + "_sum", series.Help, PrometheusExpositionWriter.GaugeType,
                    series.Samples.Where(s => s.Sum.HasValue)
                        .Select(s => new PrometheusExpositionSample(s.Labels, s.Sum!.Value)).ToList());
        }

        private static void AddHealthFamily(
            List<PrometheusExpositionFamily> families, string name, string help, List<PrometheusExpositionSample> samples)
        {
            if (samples.Count > 0)
                families.Add(new PrometheusExpositionFamily(
                    name, help, PrometheusExpositionWriter.GaugeType, samples));
        }

        private static IReadOnlyList<KeyValuePair<string, string>> MetricLabels(string exportedName) =>
            new[] { new KeyValuePair<string, string>("metric", exportedName) };

        private static double UnixSeconds(DateTimeOffset at) => at.ToUnixTimeSeconds();

        // The cache key partitions by endpoint + model version + series identity + security mode +
        // IDENTITY PARTITION. Omitting the identity partition would let one identity's cached series
        // be served to another — a cross-tenant cache leak (criterion 2, BLOCKER-class).
        internal string CacheKey(string modelToken, PrometheusMetricConfig config, IDictionary<string, object?>? userContext) =>
            string.Join("",
                _options.Endpoint,
                modelToken,
                config.SeriesKey,
                config.SecurityMode ?? "none",
                IdentityPartition(userContext));

        // A deterministic, plaintext-free fingerprint of the scope's user context. An empty context
        // (non-tenant / aggregate-with-no-claims) is its own partition; two different service
        // identities never collide.
        private static string IdentityPartition(IDictionary<string, object?>? userContext)
        {
            if (userContext is null || userContext.Count == 0)
                return "none";

            var parts = new List<string>();
            foreach (var kv in userContext)
            {
                if (kv.Value is null)
                    continue;
                parts.Add(kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            }
            parts.Sort(StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
        }
    }
}
