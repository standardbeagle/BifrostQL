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
                if (fk.IsComposite)
                    continue;

                if (!tablesByDbName.TryGetValue((fk.ChildTableSchema, fk.ChildTableName), out var childTable))
                    continue;
                if (!tablesByDbName.TryGetValue((fk.ParentTableSchema, fk.ParentTableName), out var parentTable))
                    continue;

                var childColumnName = fk.ChildColumnNames[0];
                var parentColumnName = fk.ParentColumnNames[0];

                if (!childTable.ColumnLookup.TryGetValue(childColumnName, out var childColumn))
                    continue;
                if (!parentTable.ColumnLookup.TryGetValue(parentColumnName, out var parentColumn))
                    continue;

                // Avoid duplicate links
                if (childTable.SingleLinks.ContainsKey(parentTable.GraphQlName))
                    continue;

                // Create single-link (child -> parent)
                childTable.SingleLinks.Add(parentTable.GraphQlName,
                    new TableLinkDto
                    {
                        Name = parentTable.GraphQlName,
                        ChildId = childColumn,
                        ParentId = parentColumn,
                        ChildTable = childTable,
                        ParentTable = parentTable
                    });

                // Create multi-link (parent -> children) if not exists
                if (!parentTable.MultiLinks.ContainsKey(childTable.GraphQlName))
                {
                    parentTable.MultiLinks.Add(childTable.GraphQlName,
                        new TableLinkDto
                        {
                            Name = childTable.GraphQlName,
                            ChildId = childColumn,
                            ParentId = parentColumn,
                            ChildTable = childTable,
                            ParentTable = parentTable
                        });
                }
            }
        }
    }
}
