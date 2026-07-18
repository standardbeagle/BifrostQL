using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BifrostQL.Server.Prometheus
{
    /// <summary>
    /// One sample line of a Prometheus exposition family: the (already name-sorted) label set
    /// and the numeric value. Label names are Prometheus-valid by construction (slice-1 grammar /
    /// fixed self-metric names), so only label VALUES are escaped on the wire.
    /// </summary>
    public sealed record PrometheusExpositionSample(
        IReadOnlyList<KeyValuePair<string, string>> Labels,
        double Value);

    /// <summary>
    /// A Prometheus metric family: the exported name, optional HELP text, the <c>TYPE</c> token
    /// (all business + self metrics here are <c>gauge</c> — a scrape-time aggregate snapshot, not a
    /// monotonic process counter), and its samples. The writer is the single owner of the 0.0.4
    /// text encoding; every emitted series is modeled as a family so business series and health
    /// self-metrics share one deterministic, escaped code path.
    /// </summary>
    public sealed record PrometheusExpositionFamily(
        string Name,
        string? Help,
        string Type,
        IReadOnlyList<PrometheusExpositionSample> Samples);

    /// <summary>
    /// Renders Prometheus families into the <c>text/plain; version=0.0.4</c> exposition format with
    /// HELP/TYPE header lines, Prometheus escaping (backslash + newline in HELP; backslash, newline
    /// and <c>"</c> in label values), and DETERMINISTIC ordering — families sorted by name, samples
    /// sorted by their rendered label block — so a scrape is byte-stable across repeated collections
    /// and dialects. The writer performs NO collection and holds NO state; determinism and escaping
    /// (criterion 1) live entirely here.
    /// </summary>
    public static class PrometheusExpositionWriter
    {
        /// <summary>The Prometheus 0.0.4 text exposition content type.</summary>
        public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

        /// <summary>The single TYPE used for every business + self metric (a scrape-time snapshot).</summary>
        public const string GaugeType = "gauge";

        public static string Write(IReadOnlyList<PrometheusExpositionFamily> families)
        {
            var sb = new StringBuilder();
            foreach (var family in families.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (family.Samples.Count == 0)
                    continue;

                if (family.Help is { } help)
                    sb.Append("# HELP ").Append(family.Name).Append(' ').Append(EscapeHelp(help)).Append('\n');
                sb.Append("# TYPE ").Append(family.Name).Append(' ').Append(family.Type).Append('\n');

                // Sort samples by their rendered label block so exposition order never depends on
                // the order the caller happened to accumulate them in.
                foreach (var sample in family.Samples.OrderBy(RenderLabels, StringComparer.Ordinal))
                {
                    sb.Append(family.Name).Append(RenderLabels(sample))
                        .Append(' ').Append(FormatValue(sample.Value)).Append('\n');
                }
            }

            return sb.ToString();
        }

        private static string RenderLabels(PrometheusExpositionSample sample)
        {
            if (sample.Labels.Count == 0)
                return string.Empty;

            var sb = new StringBuilder("{");
            for (var i = 0; i < sample.Labels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(sample.Labels[i].Key).Append("=\"")
                    .Append(EscapeLabelValue(sample.Labels[i].Value)).Append('"');
            }
            return sb.Append('}').ToString();
        }

        // HELP escaping: backslash first (so an escaped newline's backslash is not re-escaped), then newline.
        private static string EscapeHelp(string value) =>
            value.Replace("\\", "\\\\").Replace("\n", "\\n");

        // Label-value escaping: backslash, then newline, then the double quote.
        private static string EscapeLabelValue(string value) =>
            value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"");

        private static string FormatValue(double value) =>
            value.ToString(CultureInfo.InvariantCulture);
    }
}
