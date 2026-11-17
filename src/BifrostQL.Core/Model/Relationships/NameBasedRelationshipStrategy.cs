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

        /// <summary>
        /// Candidate parent tables by NormalizedName, schema-aware. A table's bare name
        /// is not its identity: <c>hr.employee</c> and <c>payroll.employee</c> share
        /// one. A column resolves to the same-named table in its own table's schema;
        /// failing that, to the one schema that defines the name. A name defined in two
        /// or more other schemas is not inferred — picking one would link across
        /// schemas by read order.
        /// </summary>
        private sealed class SingleKeyTableIndex
        {
            private readonly Dictionary<(string Schema, string Name), IDbTable> _bySchema = new(new SchemaTableComparer());
            private readonly Dictionary<string, List<IDbTable>> _byName = new(StringComparer.InvariantCultureIgnoreCase);

            public SingleKeyTableIndex(IEnumerable<IDbTable> tables)
            {
                foreach (var table in tables)
                {
                    // First-wins within one schema (two tables singularizing to the
                    // same name), as before.
                    if (!_bySchema.TryAdd((table.TableSchema, table.NormalizedName), table))
                        continue;
                    if (!_byName.TryGetValue(table.NormalizedName, out var list))
                    {
                        list = new List<IDbTable>();
                        _byName[table.NormalizedName] = list;
                    }
                    list.Add(table);
                }
            }

            public IDbTable? Find(string schema, string normalizedName)
            {
                if (_bySchema.TryGetValue((schema, normalizedName), out var sameSchema))
                    return sameSchema;
                return _byName.TryGetValue(normalizedName, out var candidates) && candidates.Count == 1
                    ? candidates[0]
                    : null;
            }
        }

        public NameBasedRelationshipStrategy(IReadOnlyList<PrefixGroup>? prefixGroups = null)
        {
            _prefixGroups = prefixGroups ?? Array.Empty<PrefixGroup>();
        }

        /// <inheritdoc />
        public void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            // Get columns already linked by foreign keys to avoid duplicates
            var fkLinkedColumns = GetFkLinkedColumns(model, foreignKeys);

            // Non-prefixed candidate parents, filtered to tables with exactly ONE
            // primary key column so that all downstream KeyColumns.First() calls (in
            // DiscoverRelationships and FindIdMatches) are safe — this is the single
            // guard for those three call-sites, matching the repo pattern of guarding
            // at candidate-selection time rather than scattering checks through the code.
            var singleTables = new SingleKeyTableIndex(model.Tables.Where(t => t.KeyColumns.Count() == 1));

            // Per-prefix-group lookup
            var prefixLookups = BuildPrefixLookups(model);

            // Build reverse index: table DbName -> prefix group prefix
            var tableToPrefixGroup = BuildTableToPrefixGroupIndex();

            // Find column-to-table matches
            var idMatches = FindIdMatches(model, singleTables, prefixLookups, tableToPrefixGroup, fkLinkedColumns);

            // Create links for matches
            foreach (var idMatch in idMatches)
            {
                // A child already linked to this parent (by a foreign key or an earlier name
                // match) keeps that link; a name match never adds a second one.
                if (idMatch.table.SingleLinks.Values.Any(l => ReferenceEquals(l.ParentTable, idMatch.parent)))
                    continue;

                var parentFieldName = LinkFieldNames.Unique(idMatch.table, idMatch.parent.GraphQlName);
                idMatch.table.SingleLinks.Add(parentFieldName,
                    new TableLinkDto
                    {
                        Name = idMatch.parent.GraphQlName,
                        RelationshipKind = TableLinkRelationshipKind.NameBased,
                        ParentFieldNameOverride =
                            string.Equals(parentFieldName, idMatch.parent.GraphQlName, StringComparison.Ordinal)
                                ? null
                                : parentFieldName,
                        ChildId = idMatch.column,
                        ParentId = idMatch.parent.KeyColumns.First(),
                        ChildTable = idMatch.table,
                        ParentTable = idMatch.parent
                    });

                var childFieldName = LinkFieldNames.ChildCollection(idMatch.parent, idMatch.table);
                idMatch.parent.MultiLinks.Add(childFieldName,
                    new TableLinkDto
                    {
                        Name = idMatch.table.GraphQlName,
                        RelationshipKind = TableLinkRelationshipKind.NameBased,
                        ChildFieldNameOverride = childFieldName,
                        ChildId = idMatch.column,
                        ParentId = idMatch.parent.KeyColumns.First(),
                        ChildTable = idMatch.table,
                        ParentTable = idMatch.parent
                    });
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
            SingleKeyTableIndex singleTables,
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

                return singleTables.Find(ownerTable.TableSchema, column.NormalizedName);
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
