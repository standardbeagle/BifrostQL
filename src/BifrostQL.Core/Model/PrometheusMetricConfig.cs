using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Parsed per-table Prometheus business-metric configuration. Built from the
    /// <c>metric-name</c> / <c>metric-help</c> / <c>metric-count</c> / <c>metric-sum</c>
    /// / <c>metric-labels</c> / <c>metric-max-cardinality</c> / <c>metric-security-mode</c>
    /// table metadata. A table with no <c>metric-name</c> key returns <see cref="None"/>,
    /// so nothing is exported absent the opt-in.
    ///
    /// This is a STRUCTURAL parse: it validates the metric-name grammar, the present-but-
    /// empty guards, the count/sum "at least one source" rule, the security-mode token,
    /// the cardinality integer, and intra-metric label-normalization collisions — all of
    /// which need only the table's own metadata. Column-existence, numeric-type,
    /// encrypted-label, tenant-mode, and cross-table duplicate-series checks require the
    /// model (or other tables) and live in <see cref="ModelConfigValidator"/>, which reuses
    /// this collector so validation cannot drift from the runtime parse. Throws
    /// <see cref="InvalidOperationException"/> on a structural error so a typo fails fast
    /// rather than silently exporting the wrong (or no) series.
    /// </summary>
    public sealed class PrometheusMetricConfig
    {
        // Prometheus metric-name grammar: https://prometheus.io/docs/concepts/data_model/
        private static readonly Regex MetricNameGrammar =
            new("^[a-zA-Z_:][a-zA-Z0-9_:]*$", RegexOptions.Compiled);

        /// <summary>The no-metric sentinel returned for tables that do not opt in.</summary>
        public static readonly PrometheusMetricConfig None = new(
            metricName: null, exportedName: null, help: null,
            countsAllRows: false, countColumn: null, sumColumn: null,
            labels: Array.Empty<string>(), exportedLabels: Array.Empty<string>(),
            maxCardinality: null, securityMode: null);

        private PrometheusMetricConfig(
            string? metricName,
            string? exportedName,
            string? help,
            bool countsAllRows,
            string? countColumn,
            string? sumColumn,
            IReadOnlyList<string> labels,
            IReadOnlyList<string> exportedLabels,
            int? maxCardinality,
            string? securityMode)
        {
            MetricName = metricName;
            ExportedName = exportedName;
            Help = help;
            CountsAllRows = countsAllRows;
            CountColumn = countColumn;
            SumColumn = sumColumn;
            Labels = labels;
            ExportedLabels = exportedLabels;
            MaxCardinality = maxCardinality;
            SecurityMode = securityMode;
        }

        /// <summary>Whether the table declares a metric (carries a non-blank <c>metric-name</c>).</summary>
        public bool DeclaresMetric => MetricName != null;

        /// <summary>The declared metric name verbatim, or null when the table does not opt in.</summary>
        public string? MetricName { get; }

        /// <summary>
        /// The deterministically normalized exported metric name — the series identity used
        /// for duplicate-series detection. Two declared names that normalize to the same
        /// exported name collide (a rejected duplicate series), never a silent overwrite.
        /// </summary>
        public string? ExportedName { get; }

        /// <summary>Optional HELP text (never present-but-empty — that is rejected).</summary>
        public string? Help { get; }

        /// <summary>Whether the metric counts every row (<c>COUNT(*)</c>).</summary>
        public bool CountsAllRows { get; }

        /// <summary>The column whose non-null values are counted, or null (<see cref="CountsAllRows"/> / no count).</summary>
        public string? CountColumn { get; }

        /// <summary>The numeric column summed into the metric, or null when no sum source is declared.</summary>
        public string? SumColumn { get; }

        /// <summary>Whether the metric has any count source (all-rows or a column).</summary>
        public bool HasCount => CountsAllRows || CountColumn != null;

        /// <summary>The label columns in declaration order, canonicalized to the column's database casing.</summary>
        public IReadOnlyList<string> Labels { get; }

        /// <summary>The normalized exported label names (part of the series identity), sorted.</summary>
        public IReadOnlyList<string> ExportedLabels { get; }

        /// <summary>The bounded cardinality override, or null when unset.</summary>
        public int? MaxCardinality { get; }

        /// <summary>The explicit scrape-security mode, or null when the table is not tenant-scoped.</summary>
        public string? SecurityMode { get; }

        /// <summary>
        /// The series identity — exported name plus the sorted normalized label set — used to
        /// detect duplicate-series collisions across tables. Two metrics with the same series
        /// key would clobber each other on the exposition wire.
        /// </summary>
        public string SeriesKey => ExportedName + "{" + string.Join(",", ExportedLabels) + "}";

        // Cached per table instance (the model and its metadata are immutable after load,
        // and both the validator and later collection ask for the same table's config).
        private static readonly ConditionalWeakTable<IDbTable, PrometheusMetricConfig> ConfigByTable = new();

        /// <summary>Parses the metric config for a single table (cached per table instance).</summary>
        public static PrometheusMetricConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            return ConfigByTable.GetValue(table, Parse);
        }

        private static PrometheusMetricConfig Parse(IDbTable table)
        {
            // Opt-in marker: no metric-name key at all → not a metric table.
            if (!table.Metadata.ContainsKey(MetadataKeys.Metrics.Name))
                return None;

            var nameRaw = table.GetMetadataValue(MetadataKeys.Metrics.Name);
            if (string.IsNullOrWhiteSpace(nameRaw))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.Name}' is set but empty; a metric must be named.");

            var name = nameRaw.Trim();
            if (!MetricNameGrammar.IsMatch(name))
                throw new InvalidOperationException(
                    $"metric name '{name}' is not a valid Prometheus metric name " +
                    "(must match [a-zA-Z_:][a-zA-Z0-9_:]*).");

            var help = ParseOptionalNonEmpty(table, MetadataKeys.Metrics.Help);

            var (countsAllRows, countColumn) = ParseCount(table);
            var sumColumn = ParseSum(table);
            if (!countsAllRows && countColumn is null && sumColumn is null)
                throw new InvalidOperationException(
                    $"metric '{name}' declares neither a '{MetadataKeys.Metrics.Count}' nor a " +
                    $"'{MetadataKeys.Metrics.Sum}' source; a metric must count and/or sum something.");

            var (labels, exportedLabels) = ParseLabels(table, name);
            var maxCardinality = ParseMaxCardinality(table, name);
            var securityMode = ParseSecurityMode(table, name);

            return new PrometheusMetricConfig(
                name, StringNormalizer.NormalizeName(name), help,
                countsAllRows, countColumn, sumColumn,
                labels, exportedLabels, maxCardinality, securityMode);
        }

        private static string? ParseOptionalNonEmpty(IDbTable table, string key)
        {
            if (!table.Metadata.ContainsKey(key))
                return null;
            var value = table.GetMetadataValue(key);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"'{key}' is set but empty; remove the key or give it a value.");
            return value.Trim();
        }

        private static (bool CountsAllRows, string? CountColumn) ParseCount(IDbTable table)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.Metrics.Count))
                return (false, null);

            var value = table.GetMetadataValue(MetadataKeys.Metrics.Count);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.Count}' is set but empty; use " +
                    $"'{MetadataKeys.Metrics.CountAll}' for COUNT(*) or name a column.");

            var token = value.Trim();
            if (string.Equals(token, MetadataKeys.Metrics.CountAll, StringComparison.OrdinalIgnoreCase))
                return (true, null);

            // Names a column whose non-null values are counted; existence is checked by
            // the validator (it needs the table's column set), canonicalized to DB casing.
            return (false, CanonicalizeColumn(table, token));
        }

        private static string? ParseSum(IDbTable table)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.Metrics.Sum))
                return null;

            var value = table.GetMetadataValue(MetadataKeys.Metrics.Sum);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.Sum}' is set but empty; name the numeric column to sum.");

            return CanonicalizeColumn(table, value.Trim());
        }

        private static (IReadOnlyList<string> Labels, IReadOnlyList<string> ExportedLabels) ParseLabels(
            IDbTable table, string metricName)
        {
            var labels = new List<string>();
            var exported = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in SplitList(table.GetMetadataValue(MetadataKeys.Metrics.Labels)))
            {
                var normalized = StringNormalizer.NormalizeName(raw);
                if (!seen.Add(normalized))
                    throw new InvalidOperationException(
                        $"metric '{metricName}' declares label '{raw}' that normalizes to the same exported " +
                        $"label name as an earlier label ('{normalized}'); a normalization collision would " +
                        "silently overwrite one label dimension. Remove the duplicate.");

                labels.Add(CanonicalizeColumn(table, raw));
                exported.Add(normalized);
            }

            exported.Sort(StringComparer.Ordinal);
            return (labels, exported);
        }

        private static int? ParseMaxCardinality(IDbTable table, string metricName)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.Metrics.MaxCardinality))
                return null;

            var value = table.GetMetadataValue(MetadataKeys.Metrics.MaxCardinality);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.MaxCardinality}' on metric '{metricName}' is set but empty; " +
                    "give a positive integer or remove the key.");

            if (!int.TryParse(value.Trim(), out var cardinality) || cardinality <= 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.MaxCardinality}' on metric '{metricName}' is '{value}'; " +
                    "the cardinality override must be a positive integer.");

            return cardinality;
        }

        private static string? ParseSecurityMode(IDbTable table, string metricName)
        {
            if (!table.Metadata.ContainsKey(MetadataKeys.Metrics.SecurityMode))
                return null;

            var value = table.GetMetadataValue(MetadataKeys.Metrics.SecurityMode);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.SecurityMode}' on metric '{metricName}' is set but empty; " +
                    $"choose one of {string.Join(", ", MetadataKeys.Metrics.SecurityModes)}.");

            var token = value.Trim();
            if (!MetadataKeys.Metrics.SecurityModes.Contains(token))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Metrics.SecurityMode}' on metric '{metricName}' is '{token}'; " +
                    $"valid modes: {string.Join(", ", MetadataKeys.Metrics.SecurityModes)}.");

            return token;
        }

        private static string CanonicalizeColumn(IDbTable table, string name) =>
            table.ColumnLookup.TryGetValue(name, out var column) ? column.ColumnName : name;

        private static IEnumerable<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
