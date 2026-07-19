using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Fts
{
    /// <summary>
    /// Parsed per-table full-text search configuration. Built from the <c>search</c> /
    /// <c>search-language</c> table metadata. A table with no <c>search</c> key returns
    /// <see cref="None"/>. Mirrors the <c>HistoryConfig.FromTable</c> /
    /// <c>CdcEventConfig.FromTable</c> factory shape: a per-table metadata read, cached by
    /// table identity, that fails fast on a config that would otherwise silently do the
    /// wrong thing.
    ///
    /// The <c>_search</c> operator (see <see cref="Core.QueryModel.FilterOperators.Search"/>)
    /// is TABLE-scoped — it matches one query string against the resolved
    /// <see cref="SearchColumns"/> — so the schema surfaces it on the table's filter input
    /// only when <see cref="IsSearchable"/> is true. The per-dialect SQL lowering (a later
    /// FTS sub-task) consumes this config; the pinned multi-term/phrase semantic it must
    /// implement is documented on <see cref="Core.QueryModel.FilterOperators.Search"/>.
    /// </summary>
    public sealed class FtsConfig
    {
        /// <summary>The not-searchable sentinel returned for tables that do not opt in.</summary>
        public static readonly FtsConfig None = new(Array.Empty<string>(), language: null);

        private FtsConfig(IEnumerable<string> searchColumns, string? language)
        {
            SearchColumns = searchColumns.ToList();
            Language = language;
        }

        /// <summary>Whether the table declares any searchable column (the <c>search</c> opt-in).</summary>
        public bool IsSearchable => SearchColumns.Count > 0;

        /// <summary>
        /// The columns named by <c>search</c>, in declaration order, each canonicalized to
        /// the column's database casing. Empty for a non-searchable table.
        /// </summary>
        public IReadOnlyList<string> SearchColumns { get; }

        /// <summary>
        /// The <c>search-language</c> hint, or null when omitted. Advisory in this slice —
        /// consumed by the per-dialect lowering sub-task.
        /// </summary>
        public string? Language { get; }

        /// <summary>
        /// Database types accepted for a searchable column across the supported dialects.
        /// A <c>search</c> list naming a non-text column is a configuration error: a
        /// full-text match over a numeric/date column is meaningless. Mirrors
        /// <c>ChatConfig.StringColumnTypes</c>.
        /// </summary>
        public static readonly IReadOnlySet<string> StringColumnTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "varchar", "nvarchar", "char", "nchar", "text", "ntext",
                "tinytext", "mediumtext", "longtext", "clob", "citext",
                "character", "character varying",
            };

        // Cached per table instance for the same reason as HistoryConfig: schema generation
        // and ModelConfigValidator both ask for it, and the model is immutable after load.
        // A parse that throws (invalid column) is never cached — each caller sees the failure.
        private static readonly ConditionalWeakTable<IDbTable, FtsConfig> ConfigByTable = new();

        /// <summary>
        /// Parses the FTS config for a single table (cached per table instance). Throws
        /// <see cref="InvalidOperationException"/> when the <c>search</c> list is present but
        /// names no column, names a duplicate column, or names a column that does not exist
        /// or is not a string type — each of which would otherwise leave a table that reads
        /// as searchable in config either non-searchable or matching a meaningless column,
        /// with no error until a query arrives.
        /// </summary>
        public static FtsConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            return ConfigByTable.GetValue(table, Parse);
        }

        private static FtsConfig Parse(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.Fts.Search);
            if (string.IsNullOrWhiteSpace(raw))
                return None;

            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SplitList(raw))
            {
                if (!seen.Add(name))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Fts.Search}' on '{table.TableSchema}.{table.DbName}' lists column " +
                        $"'{name}' more than once (column names match case-insensitively); remove the duplicate.");

                if (!table.ColumnLookup.TryGetValue(name, out var column))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Fts.Search}' on '{table.TableSchema}.{table.DbName}' names column " +
                        $"'{name}' which does not exist on the table; a search over a missing column matches nothing.");

                if (!StringColumnTypes.Contains(StringNormalizer.NormalizeType(column.DataType)))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Fts.Search}' on '{table.TableSchema}.{table.DbName}' names column " +
                        $"'{column.ColumnName}' of type '{column.DataType}', but a searchable column must be a " +
                        $"string type ({string.Join(", ", StringColumnTypes.OrderBy(t => t, StringComparer.Ordinal))}).");

                columns.Add(column.ColumnName);
            }

            // 'search' is present (non-blank) but names no valid column — e.g. "," or ", ,".
            // RemoveEmptyEntries would leave a zero-length list, which reads as
            // IsSearchable=false: the table is SILENTLY not searchable despite declaring it.
            // Same fail-open the CDC/History empty-list guards reject.
            if (columns.Count == 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Fts.Search}' on '{table.TableSchema}.{table.DbName}' is set but names no " +
                    "column; provide a comma-separated list of string columns to search.");

            var language = table.GetMetadataValue(MetadataKeys.Fts.SearchLanguage);
            return new FtsConfig(columns, string.IsNullOrWhiteSpace(language) ? null : language.Trim());
        }

        private static IEnumerable<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
