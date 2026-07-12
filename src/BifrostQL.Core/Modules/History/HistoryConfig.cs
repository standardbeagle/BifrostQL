using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.History
{
    /// <summary>
    /// Parsed per-table temporal-history configuration. Built from the <c>history</c> /
    /// <c>history-table</c> / <c>history-columns</c> table metadata. A table with no
    /// <c>history</c> key returns <see cref="None"/>.
    ///
    /// The history TARGET is resolved against the model (a table-level
    /// <c>history-table</c> overrides the model-level default), which this per-table
    /// parse cannot see; <see cref="HistoryTableOverride"/> exposes the table-level
    /// value and ModelConfigValidator performs the resolution + existence check.
    /// </summary>
    public sealed class HistoryConfig
    {
        /// <summary>The no-history sentinel returned for tables that do not opt in.</summary>
        public static readonly HistoryConfig None = new(
            Array.Empty<MutationType>(), Array.Empty<string>(), historyTableOverride: null);

        private readonly IReadOnlySet<MutationType> _operations;
        private readonly IReadOnlySet<string> _trackedColumns;

        private HistoryConfig(
            IEnumerable<MutationType> operations,
            IEnumerable<string> trackedColumns,
            string? historyTableOverride)
        {
            _operations = new HashSet<MutationType>(operations);
            TrackedColumns = trackedColumns.ToList();
            _trackedColumns = new HashSet<string>(TrackedColumns, StringComparer.OrdinalIgnoreCase);
            HistoryTableOverride = historyTableOverride;
        }

        /// <summary>Whether the table records history (at least one operation opted in).</summary>
        public bool RecordsHistory => _operations.Count > 0;

        /// <summary>
        /// The columns named by <c>history-columns</c>, in declaration order. Empty means
        /// the key was omitted and every column of the table is tracked — see
        /// <see cref="TracksAllColumns"/>.
        /// </summary>
        public IReadOnlyList<string> TrackedColumns { get; }

        /// <summary>Whether every column is tracked (no <c>history-columns</c> allow-list).</summary>
        public bool TracksAllColumns => TrackedColumns.Count == 0;

        /// <summary>
        /// The table-level <c>history-table</c> value, or null when the table defers to the
        /// model-level default. Unresolved — see the class summary.
        /// </summary>
        public string? HistoryTableOverride { get; }

        /// <summary>Whether <paramref name="operation"/> writes a history row.</summary>
        public bool Records(MutationType operation) => _operations.Contains(operation);

        /// <summary>Whether changes to <paramref name="column"/> are recorded.</summary>
        public bool TracksColumn(string column) =>
            TracksAllColumns || _trackedColumns.Contains(column);

        /// <summary>
        /// Parses the history config for a single table. Throws
        /// <see cref="InvalidOperationException"/> on an unrecognized operation token so a
        /// typo fails fast rather than silently leaving the table with no trail — a hole in
        /// an audit trail is invisible precisely when it matters (a dispute, a rollback).
        /// </summary>
        public static HistoryConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            var enabledRaw = table.GetMetadataValue(MetadataKeys.History.Enabled);
            if (string.IsNullOrWhiteSpace(enabledRaw))
                return None;

            var operations = ParseOperations(enabledRaw).ToList();

            // 'history' is present (non-blank) but names no valid operation — e.g. "," or
            // ", ,". RemoveEmptyEntries would leave a zero-length set, which reads as
            // RecordsHistory=false: the table is SILENTLY opted out of the very trail it
            // declared. Same fail-open the CDC emit-events guard rejects.
            if (operations.Count == 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.History.Enabled}' is set but names no valid operation. " +
                    $"Provide '{MetadataKeys.History.AllOperations}' or a comma-separated subset of " +
                    $"{string.Join(", ", OperationNames)}.");

            var trackedColumns = ParseTrackedColumns(table);

            var overrideTable = table.GetMetadataValue(MetadataKeys.History.Table);
            if (string.IsNullOrWhiteSpace(overrideTable))
                overrideTable = null;

            return new HistoryConfig(operations, trackedColumns, overrideTable?.Trim());
        }

        /// <summary>
        /// Parses <c>history-columns</c>. A case-insensitive duplicate is rejected rather
        /// than deduped — the writer keys its projected images by tracked column in a
        /// case-insensitive dictionary, so a duplicate would crash every recorded write,
        /// and silently merging it would hide a config whose intent is ambiguous. Each
        /// entry is canonicalized to the column's database casing: tracked names become
        /// SQL identifiers (quoted verbatim on Postgres) and trail JSON keys. An entry
        /// naming no column is kept verbatim for ModelConfigValidator to report.
        /// </summary>
        private static List<string> ParseTrackedColumns(IDbTable table)
        {
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SplitList(table.GetMetadataValue(MetadataKeys.History.Columns)))
            {
                if (!seen.Add(name))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.History.Columns}' on '{table.TableSchema}.{table.DbName}' lists " +
                        $"column '{name}' more than once (column names match case-insensitively); remove the duplicate.");

                columns.Add(table.ColumnLookup.TryGetValue(name, out var column) ? column.ColumnName : name);
            }
            return columns;
        }

        private static IEnumerable<MutationType> ParseOperations(string raw)
        {
            foreach (var token in SplitList(raw))
            {
                if (string.Equals(token, MetadataKeys.History.AllOperations, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var op in Enum.GetValues<MutationType>())
                        yield return op;
                    continue;
                }

                if (Enum.TryParse<MutationType>(token, ignoreCase: true, out var operation))
                {
                    yield return operation;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unknown history operation '{token}' in '{MetadataKeys.History.Enabled}'. " +
                    $"Valid values: {MetadataKeys.History.AllOperations}, {string.Join(", ", OperationNames)}.");
            }
        }

        private static IEnumerable<string> OperationNames =>
            Enum.GetNames<MutationType>().Select(n => n.ToLowerInvariant());

        private static IEnumerable<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
