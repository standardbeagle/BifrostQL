using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BifrostQL.Core.Observers;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// Renders the Bifrost ENGINE self-metrics (<see cref="EngineMetrics"/>) into 0.0.4 exposition
    /// text, SEPARATELY from the database-derived business series (slices 1-4). Every engine series
    /// carries the <c>bifrostql_engine_</c> prefix, counters end in <c>_total</c>, and duration
    /// histograms end in <c>_seconds</c> (criterion 4 — Prometheus naming/unit stability). Only
    /// bounded enum labels (<c>operation</c>, <c>outcome</c>, <c>adapter</c>) ever reach the wire —
    /// the snapshot the writer is handed cannot carry any other dimension (criterion 2).
    ///
    /// <para>Counter and gauge families reuse <see cref="PrometheusExpositionWriter"/>; histograms are
    /// rendered here with cumulative <c>le</c> buckets (ascending, <c>+Inf</c> last) plus <c>_sum</c>
    /// and <c>_count</c> under one <c># TYPE ... histogram</c> header, which the gauge-oriented writer
    /// does not model.</para>
    /// </summary>
    public static class EngineMetricsExposition
    {
        public const string RequestsMetric = "bifrostql_engine_requests_total";
        public const string SqlDurationMetric = "bifrostql_engine_sql_duration_seconds";
        public const string TransformerDurationMetric = "bifrostql_engine_transformer_duration_seconds";
        public const string ActiveConnectionsMetric = "bifrostql_engine_active_connections";

        private const string CounterType = "counter";
        private const string HistogramType = "histogram";

        /// <summary>Renders the whole engine self-metric block. Empty string when nothing was recorded.</summary>
        public static string Render(EngineMetricsSnapshot snapshot)
        {
            var flat = new List<PrometheusExpositionFamily>
            {
                new(RequestsMetric,
                    "Total engine requests by operation and outcome.",
                    CounterType,
                    snapshot.Requests
                        .Where(r => r.Count > 0)
                        .Select(r => new PrometheusExpositionSample(
                            new[]
                            {
                                Label("operation", OperationLabel(r.Operation)),
                                Label("outcome", OutcomeLabel(r.Outcome)),
                            },
                            r.Count))
                        .ToList()),
                new(ActiveConnectionsMetric,
                    "Currently active adapter connections by adapter.",
                    PrometheusExpositionWriter.GaugeType,
                    snapshot.Connections
                        .Where(c => c.Active != 0)
                        .Select(c => new PrometheusExpositionSample(
                            new[] { Label("adapter", AdapterLabel(c.Adapter)) },
                            c.Active))
                        .ToList()),
            };

            var sb = new StringBuilder();
            sb.Append(PrometheusExpositionWriter.Write(flat));
            WriteHistogram(sb, SqlDurationMetric,
                "Engine SQL execution duration in seconds by operation.", snapshot.SqlDurations);
            WriteHistogram(sb, TransformerDurationMetric,
                "Engine transformer-pipeline duration in seconds by operation.", snapshot.TransformerDurations);
            return sb.ToString();
        }

        // Renders one histogram family: one TYPE header, then per-operation cumulative _bucket lines
        // (ascending le, +Inf last) followed by that operation's _sum and _count. Operations with no
        // observations are skipped so an idle histogram emits nothing.
        private static void WriteHistogram(
            StringBuilder sb, string name, string help, IReadOnlyList<EngineHistogramReading> readings)
        {
            var active = readings.Where(r => r.Count > 0)
                .OrderBy(r => OperationLabel(r.Operation), System.StringComparer.Ordinal)
                .ToList();
            if (active.Count == 0)
                return;

            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(HistogramType).Append('\n');

            foreach (var reading in active)
            {
                var op = OperationLabel(reading.Operation);
                foreach (var bucket in reading.Buckets)
                {
                    sb.Append(name).Append("_bucket{operation=\"").Append(op)
                        .Append("\",le=\"").Append(FormatLe(bucket.UpperBound)).Append("\"} ")
                        .Append(bucket.CumulativeCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                sb.Append(name).Append("_sum{operation=\"").Append(op).Append("\"} ")
                    .Append(reading.Sum.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(name).Append("_count{operation=\"").Append(op).Append("\"} ")
                    .Append(reading.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        private static KeyValuePair<string, string> Label(string key, string value) => new(key, value);

        private static string FormatLe(double upperBound) =>
            double.IsPositiveInfinity(upperBound)
                ? "+Inf"
                : upperBound.ToString(CultureInfo.InvariantCulture);

        private static string OperationLabel(EngineOperation operation) =>
            operation == EngineOperation.Write ? "write" : "read";

        private static string OutcomeLabel(EngineRequestOutcome outcome) => outcome switch
        {
            EngineRequestOutcome.Success => "success",
            EngineRequestOutcome.Error => "error",
            EngineRequestOutcome.Denied => "denied",
            _ => "success",
        };

        private static string AdapterLabel(EngineAdapter adapter) => adapter switch
        {
            EngineAdapter.GraphQL => "graphql",
            EngineAdapter.OData => "odata",
            EngineAdapter.Grpc => "grpc",
            EngineAdapter.Prometheus => "prometheus",
            EngineAdapter.Pgwire => "pgwire",
            EngineAdapter.Resp => "resp",
            EngineAdapter.Mcp => "mcp",
            EngineAdapter.S3 => "s3",
            _ => "other",
        };
    }
}
