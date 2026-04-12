using Pluralize.NET.Core;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Discovers table relationships by analyzing column names and matching them to table names.
    /// Handles prefix-aware matching (e.g., wp_users table with user_id column from wp_posts).
    /// </summary>
    public sealed class NameBasedRelationshipStrategy : ITableRelationshipStrategy
    {
        private sealed class SchemaTableComparer : IEqualityComparer<(string Schema, string Name)>
        {
            public bool Equals((string Schema, string Name) x, (string Schema, string Name) y)
                => StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

            public int GetHashCode((string Schema, string Name) obj)
                => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema) ^
                   StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
        }

        private sealed class SchemaTableColumnComparer : IEqualityComparer<(string TableSchema, string TableName, string ColumnName)>
        {
            public bool Equals((string TableSchema, string TableName, string ColumnName) x, 
                               (string TableSchema, string TableName, string ColumnName) y)
                => StringComparer.OrdinalIgnoreCase.Equals(x.TableSchema, y.TableSchema) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.TableName, y.TableName) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.ColumnName, y.ColumnName);

            public int GetHashCode((string TableSchema, string TableName, string ColumnName) obj)
                => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableSchema) ^
                   StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableName) ^
                   StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ColumnName);
        }
        private readonly IReadOnlyList<PrefixGroup> _prefixGroups;

        public NameBasedRelationshipStrategy(IReadOnlyList<PrefixGroup>? prefixGroups = null)
        {
            _prefixGroups = prefixGroups ?? Array.Empty<PrefixGroup>();
        }

        /// <inheritdoc />
        public void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            // Get columns already linked by foreign keys to avoid duplicates
            var fkLinkedColumns = GetFkLinkedColumns(model, foreignKeys);

            // Global lookup: NormalizedName -> table (for non-prefixed matching)
            var singleTables = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var table in model.Tables.Where(t => t.KeyColumns.Count() == 1))
            {
                singleTables.TryAdd(table.NormalizedName, table);
            }

            // Per-prefix-group lookup
            var prefixLookups = BuildPrefixLookups(model);

            // Build reverse index: table DbName -> prefix group prefix
            var tableToPrefixGroup = BuildTableToPrefixGroupIndex();

            // Find column-to-table matches
            var idMatches = FindIdMatches(model, singleTables, prefixLookups, tableToPrefixGroup, fkLinkedColumns);

            // Create links for matches
            foreach (var idMatch in idMatches)
            {
                if (idMatch.table.SingleLinks.ContainsKey(idMatch.parent.GraphQlName))
                    continue;

                idMatch.table.SingleLinks.Add(idMatch.parent.GraphQlName,
                    new TableLinkDto
                    {
                        Name = idMatch.parent.GraphQlName,
                        ChildId = idMatch.column,
                        ParentId = idMatch.parent.KeyColumns.First(),
                        ChildTable = idMatch.table,
                        ParentTable = idMatch.parent
                    });

                if (!idMatch.parent.MultiLinks.ContainsKey(idMatch.table.GraphQlName))
                {
                    idMatch.parent.MultiLinks.Add(idMatch.table.GraphQlName,
                        new TableLinkDto
                        {
                            Name = idMatch.table.GraphQlName,
                            ChildId = idMatch.column,
                            ParentId = idMatch.parent.KeyColumns.First(),
                            ChildTable = idMatch.table,
                            ParentTable = idMatch.parent
                        });
                }
            }
        }

        private HashSet<(string TableSchema, string TableName, string ColumnName)> GetFkLinkedColumns(
            IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            var tablesByDbName = model.Tables
                .ToDictionary(t => (Schema: t.TableSchema, Name: t.DbName),
                    t => t,
                    new SchemaTableComparer());

            var linked = new HashSet<(string TableSchema, string TableName, string ColumnName)>(
                new SchemaTableColumnComparer());

            foreach (var fk in foreignKeys.Where(fk => !fk.IsComposite))
            {
                if (tablesByDbName.TryGetValue((fk.ChildTableSchema, fk.ChildTableName), out _))
                {
                    linked.Add((fk.ChildTableSchema, fk.ChildTableName, fk.ChildColumnNames[0]));
                }
            }

            return linked;
        }

        private Dictionary<string, Dictionary<string, IDbTable>> BuildPrefixLookups(IDbModel model)
        {
            var prefixLookups = new Dictionary<string, Dictionary<string, IDbTable>>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var group in _prefixGroups)
            {
                var lookup = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var table in model.Tables)
                {
                    if (!group.TableDbNames.Contains(table.DbName))
                        continue;
                    if (table.KeyColumns.Count() != 1)
                        continue;

                    var stripped = table.DbName.Substring(group.Prefix.Length);
                    var normalized = DbModel.Pluralizer.Singularize(stripped);
                    lookup.TryAdd(normalized, table);
                }
                prefixLookups[group.Prefix] = lookup;
            }

            return prefixLookups;
        }

        private Dictionary<string, string> BuildTableToPrefixGroupIndex()
        {
            var tableToPrefixGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _prefixGroups)
            {
                foreach (var tableName in group.TableDbNames)
                {
                    tableToPrefixGroup.TryAdd(tableName, group.Prefix);
                }
            }
            return tableToPrefixGroup;
        }

        private IEnumerable<(ColumnDto column, IDbTable table, IDbTable parent)> FindIdMatches(
            IDbModel model,
            Dictionary<string, IDbTable> singleTables,
            Dictionary<string, Dictionary<string, IDbTable>> prefixLookups,
            Dictionary<string, string> tableToPrefixGroup,
            HashSet<(string TableSchema, string TableName, string ColumnName)> fkLinkedColumns)
        {
            IDbTable? FindParentTable(IDbTable ownerTable, ColumnDto column)
            {
                // Try prefix-stripped lookup first
                if (tableToPrefixGroup.TryGetValue(ownerTable.DbName, out var prefix) &&
                    prefixLookups.TryGetValue(prefix, out var prefixLookup) &&
                    prefixLookup.TryGetValue(column.NormalizedName, out var prefixMatch))
                {
                    return prefixMatch;
                }

                // Fall back to global lookup
                if (singleTables.TryGetValue(column.NormalizedName, out var globalMatch))
                    return globalMatch;

                return null;
            }

            return model.Tables
                .SelectMany(table => table.Columns.Select(column => (table, column)))
                .Select(c => (c.table, c.column, parent: FindParentTable(c.table, c.column)))
                .Where(c => c.parent != null)
                .Where(c => string.Equals(c.column.NormalizedName, c.table.NormalizedName,
                    StringComparison.InvariantCultureIgnoreCase) == false)
                .Where(c => !fkLinkedColumns.Contains((c.table.TableSchema, c.table.DbName, c.column.ColumnName)))
                .Where(c => c.column.DataType == c.parent!.KeyColumns.First().DataType)
                .Select(c => (c.column, c.table, parent: c.parent!));
        }
    }
}
