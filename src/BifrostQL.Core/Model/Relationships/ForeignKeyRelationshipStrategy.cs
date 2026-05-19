namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Discovers table relationships from database foreign key constraints.
    /// Creates single-links (child -> parent) and multi-links (parent -> children).
    /// </summary>
    public sealed class ForeignKeyRelationshipStrategy : ITableRelationshipStrategy
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

        /// <inheritdoc />
        public void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            var tablesByDbName = model.Tables
                .ToDictionary(t => (Schema: t.TableSchema, Name: t.DbName),
                    t => t,
                    new SchemaTableComparer());

            foreach (var fk in foreignKeys)
            {
                if (!tablesByDbName.TryGetValue((fk.ChildTableSchema, fk.ChildTableName), out var childTable))
                    continue;
                if (!tablesByDbName.TryGetValue((fk.ParentTableSchema, fk.ParentTableName), out var parentTable))
                    continue;

                // Resolve every column on both sides of the FK; if any column
                // is unknown to the loaded model, skip the whole FK so we
                // never produce a half-formed link.
                var childColumns = new List<ColumnDto>(fk.ChildColumnNames.Count);
                var parentColumns = new List<ColumnDto>(fk.ParentColumnNames.Count);
                var resolved = true;
                for (var i = 0; i < fk.ChildColumnNames.Count; i++)
                {
                    if (!childTable.ColumnLookup.TryGetValue(fk.ChildColumnNames[i], out var childCol)
                        || !parentTable.ColumnLookup.TryGetValue(fk.ParentColumnNames[i], out var parentCol))
                    {
                        resolved = false;
                        break;
                    }
                    childColumns.Add(childCol);
                    parentColumns.Add(parentCol);
                }
                if (!resolved) continue;

                // Avoid duplicate links keyed by parent's GraphQL name.
                if (childTable.SingleLinks.ContainsKey(parentTable.GraphQlName))
                    continue;

                // Create single-link (child -> parent). ChildId/ParentId
                // remain the first column for back-compat; the full ordered
                // lists power multi-column ON-clauses in SQL emission.
                childTable.SingleLinks.Add(parentTable.GraphQlName,
                    new TableLinkDto
                    {
                        Name = parentTable.GraphQlName,
                        ChildId = childColumns[0],
                        ParentId = parentColumns[0],
                        ChildIds = childColumns,
                        ParentIds = parentColumns,
                        ChildTable = childTable,
                        ParentTable = parentTable
                    });

                var childFieldName = UniqueChildFieldName(parentTable, childTable);

                // Create multi-link (parent -> children) if not exists
                if (!parentTable.MultiLinks.ContainsKey(childFieldName))
                {
                    parentTable.MultiLinks.Add(childFieldName,
                        new TableLinkDto
                        {
                            Name = childTable.GraphQlName,
                            ChildFieldNameOverride = childFieldName,
                            ChildId = childColumns[0],
                            ParentId = parentColumns[0],
                            ChildIds = childColumns,
                            ParentIds = parentColumns,
                            ChildTable = childTable,
                            ParentTable = parentTable
                        });
                }
            }
        }

        internal static string ResolveChildFieldNameForTest(IDbTable parentTable, IDbTable childTable) =>
            UniqueChildFieldName(parentTable, childTable);

        private static string UniqueChildFieldName(IDbTable parentTable, IDbTable childTable)
        {
            var baseName = string.Equals(parentTable.GraphQlName, childTable.GraphQlName, StringComparison.OrdinalIgnoreCase)
                ? $"{childTable.GraphQlName}_children"
                : childTable.GraphQlName;
            var name = baseName;
            var i = 2;
            while (parentTable.SingleLinks.ContainsKey(name)
                || parentTable.MultiLinks.ContainsKey(name)
                || parentTable.ManyToManyLinks.ContainsKey(name))
            {
                name = $"{baseName}_{i++}";
            }
            return name;
        }
    }
}
