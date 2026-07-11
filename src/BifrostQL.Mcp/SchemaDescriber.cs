using System.Text.Json.Nodes;
using BifrostQL.Core.Model;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Shapes an <see cref="IDbModel"/> into the curated JSON payloads the MCP
    /// schema tools and resources return. Pure data shaping — no MCP types, no
    /// I/O — so the payload contract is directly testable.
    ///
    /// <para>Deliberate omissions (dev-mode slice): row-count hints are not
    /// included because they require a per-table scan against the live database,
    /// and sample values are not included because they would read row data — both
    /// belong to a future query-capable slice, not the metadata-only one.</para>
    ///
    /// <para>Metadata-derived behavior (tenant isolation, soft-delete) surfaces as
    /// plain-English behavior notes; raw metadata key names are never exposed.</para>
    /// </summary>
    public static class SchemaDescriber
    {
        /// <summary>
        /// Builds the <c>bifrost_schema_overview</c> payload: one flat entry per
        /// table with keys, relationship edges, and behavior notes.
        /// <paramref name="fullDetail"/> additionally inlines a condensed column
        /// list per table.
        /// </summary>
        public static JsonObject BuildOverview(IDbModel model, bool fullDetail)
        {
            var tables = new JsonArray();
            foreach (var table in model.Tables.OrderBy(t => t.DbName, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new JsonObject
                {
                    ["name"] = table.DbName,
                    ["schema"] = table.TableSchema,
                    ["primaryKey"] = new JsonArray(table.KeyColumns.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                    ["columnCount"] = table.Columns.Count(),
                    ["references"] = new JsonArray(OutgoingLinks(table)
                        .Select(l => (JsonNode?)$"{ColumnList(l.ChildIds)} -> {l.ParentTable.DbName}.{ColumnList(l.ParentIds)}")
                        .ToArray()),
                    ["referencedBy"] = new JsonArray(IncomingLinks(table)
                        .Select(l => (JsonNode?)$"{l.ChildTable.DbName}.{ColumnList(l.ChildIds)} -> {ColumnList(l.ParentIds)}")
                        .ToArray()),
                    ["notes"] = new JsonArray(ShortBehaviorNotes(table).Select(n => (JsonNode?)n).ToArray()),
                };
                if (fullDetail)
                {
                    entry["columns"] = new JsonArray(table.Columns
                        .OrderBy(c => c.OrdinalPosition)
                        .Select(c => (JsonNode?)CondensedColumn(c))
                        .ToArray());
                }
                tables.Add(entry);
            }

            return new JsonObject
            {
                ["detail"] = fullDetail ? "full" : "summary",
                ["tableCount"] = model.Tables.Count,
                ["tables"] = tables,
            };
        }

        /// <summary>
        /// Builds the <c>bifrost_describe_table</c> payload for
        /// <paramref name="table"/>: columns with types/keys, foreign keys in both
        /// directions, and metadata-derived behavior notes.
        /// </summary>
        public static JsonObject BuildTableDescription(IDbTable table)
        {
            var columns = new JsonArray(table.Columns
                .OrderBy(c => c.OrdinalPosition)
                .Select(c => (JsonNode?)new JsonObject
                {
                    ["name"] = c.ColumnName,
                    ["type"] = c.DataType,
                    ["nullable"] = c.IsNullable,
                    ["primaryKey"] = c.IsPrimaryKey,
                    ["identity"] = c.IsIdentity,
                    ["unique"] = c.IsUnique,
                })
                .ToArray());

            var foreignKeysOut = new JsonArray(OutgoingLinks(table)
                .Select(l => (JsonNode?)new JsonObject
                {
                    ["columns"] = new JsonArray(l.ChildIds.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                    ["referencesTable"] = l.ParentTable.DbName,
                    ["referencesColumns"] = new JsonArray(l.ParentIds.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                })
                .ToArray());

            var foreignKeysIn = new JsonArray(IncomingLinks(table)
                .Select(l => (JsonNode?)new JsonObject
                {
                    ["table"] = l.ChildTable.DbName,
                    ["columns"] = new JsonArray(l.ChildIds.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                    ["referencesColumns"] = new JsonArray(l.ParentIds.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                })
                .ToArray());

            return new JsonObject
            {
                ["table"] = table.DbName,
                ["schema"] = table.TableSchema,
                ["primaryKey"] = new JsonArray(table.KeyColumns.Select(c => (JsonNode?)c.ColumnName).ToArray()),
                ["columns"] = columns,
                ["foreignKeysOut"] = foreignKeysOut,
                ["foreignKeysIn"] = foreignKeysIn,
                ["behaviorNotes"] = new JsonArray(BehaviorNotes(table).Select(n => (JsonNode?)n).ToArray()),
            };
        }

        /// <summary>
        /// Prompt-style message for a table name the model does not contain:
        /// nearest-name suggestion plus the full table list, so an agent can
        /// self-correct without another round trip. An empty model (no tables
        /// exposed) still yields a prompt-style message rather than throwing,
        /// so agents get an actionable error instead of a protocol fault.
        /// </summary>
        public static string UnknownTableMessage(IDbModel model, string requestedTable)
        {
            var names = model.Tables
                .Select(t => t.DbName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0)
                return $"Unknown table '{requestedTable}'. No tables are available in this schema.";
            return $"Unknown table '{requestedTable}'.{DidYouMean(names, requestedTable)} Available tables: {string.Join(", ", names)}.";
        }

        /// <summary>
        /// Prompt-style message for a column name that does not exist on
        /// <paramref name="table"/>: nearest-name suggestion plus the full column
        /// list, mirroring <see cref="UnknownTableMessage"/> so filter/field/sort
        /// mistakes are self-correctable in one round trip.
        /// </summary>
        internal static string UnknownColumnMessage(IDbTable table, string requestedColumn)
        {
            var names = table.Columns
                .OrderBy(c => c.OrdinalPosition)
                .Select(c => c.ColumnName)
                .ToArray();
            return $"Unknown column '{requestedColumn}' on table '{table.DbName}'." +
                $"{DidYouMean(names, requestedColumn)} Available columns: {string.Join(", ", names)}.";
        }

        /// <summary>" Did you mean 'x'?" when the nearest candidate is plausibly a typo, else empty.</summary>
        private static string DidYouMean(IReadOnlyList<string> names, string requested)
        {
            var nearest = names
                .Select(n => (Name: n, Distance: LevenshteinDistance(requested, n)))
                .OrderBy(x => x.Distance)
                .First();
            return nearest.Distance <= Math.Max(2, requested.Length / 2)
                ? $" Did you mean '{nearest.Name}'?"
                : string.Empty;
        }

        /// <summary>
        /// Picks the human-readable "display" column for a table's summary rows.
        /// Heuristic (deliberately simple, documented for tuning): prefer a column
        /// literally named <c>name</c>, <c>title</c>, or <c>label</c> — in that
        /// priority order, case-insensitive — else fall back to the first
        /// string-typed column in ordinal order. Null when the table has no
        /// string column at all (e.g. a pure numeric junction table).
        /// </summary>
        internal static ColumnDto? DisplayColumn(IDbTable table)
        {
            foreach (var preferred in new[] { "name", "title", "label" })
            {
                var byName = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.ColumnName, preferred, StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                    return byName;
            }
            return table.Columns
                .OrderBy(c => c.OrdinalPosition)
                .FirstOrDefault(c => IsStringType(c.DataType));
        }

        internal static bool IsStringType(string dataType)
        {
            var normalized = dataType.ToLowerInvariant();
            return normalized.Contains("char") || normalized.Contains("text") || normalized.Contains("string");
        }

        /// <summary>
        /// Links where <paramref name="table"/> holds the foreign-key columns
        /// (child side) — the table's outgoing references.
        /// </summary>
        internal static IEnumerable<TableLinkDto> OutgoingLinks(IDbTable table) =>
            table.SingleLinks.Values
                .Where(l => SameTable(l.ChildTable, table))
                .OrderBy(l => l.ParentTable.DbName, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Links where <paramref name="table"/> is the referenced (parent) side —
        /// other tables' foreign keys pointing at it.
        /// </summary>
        internal static IEnumerable<TableLinkDto> IncomingLinks(IDbTable table) =>
            table.MultiLinks.Values
                .Where(l => SameTable(l.ParentTable, table))
                .OrderBy(l => l.ChildTable.DbName, StringComparer.OrdinalIgnoreCase);

        private static bool SameTable(IDbTable a, IDbTable b) =>
            string.Equals(a.DbName, b.DbName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.TableSchema, b.TableSchema, StringComparison.OrdinalIgnoreCase);

        private static string ColumnList(IReadOnlyList<ColumnDto> columns) =>
            string.Join("+", columns.Select(c => c.ColumnName));

        private static string CondensedColumn(ColumnDto c)
        {
            var markers = string.Concat(
                c.IsPrimaryKey ? " pk" : string.Empty,
                c.IsIdentity ? " identity" : string.Empty,
                c.IsUnique && !c.IsPrimaryKey ? " unique" : string.Empty,
                c.IsNullable ? " null" : string.Empty);
            return $"{c.ColumnName}: {c.DataType}{markers}";
        }

        private static IEnumerable<string> ShortBehaviorNotes(IDbTable table)
        {
            if (table.GetMetadataValue(MetadataKeys.Security.TenantFilter) is not null)
                yield return "rows are tenant-scoped";
            if (table.GetMetadataValue(MetadataKeys.SoftDelete.Column) is not null)
                yield return "soft-deleted rows hidden";
        }

        private static IEnumerable<string> BehaviorNotes(IDbTable table)
        {
            if (table.GetMetadataValue(MetadataKeys.Security.TenantFilter) is { } tenantColumn)
                yield return $"Rows are tenant-scoped: reads and writes are automatically restricted to the caller's tenant via the '{tenantColumn}' column, and requests without a tenant identity are rejected.";
            if (table.GetMetadataValue(MetadataKeys.SoftDelete.Column) is { } deleteColumn)
                yield return $"Soft-deleted rows are hidden: rows with a value in '{deleteColumn}' never appear in reads, and deletes mark the row deleted instead of removing it.";
        }

        private static int LevenshteinDistance(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }
    }
}
