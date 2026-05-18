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
                // Composite FKs are now carried through the model
                // (TableLinkDto.ChildIds/ParentIds) but the SQL emission
                // pipeline still emits single-column ON clauses through
                // GqlObjectQuery + ReaderEnum. Linking a composite FK
                // here would produce a join that only matches on the
                // first column pair — silently incorrect. Until the SQL
                // emitter and reader handle multi-column join keys, keep
                // the explicit skip so the gap is visible rather than
                // a wrong result. Tracked in worktrack workspace `bifrostql`.
                if (fk.IsComposite)
                    continue;

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

                // Create multi-link (parent -> children) if not exists
                if (!parentTable.MultiLinks.ContainsKey(childTable.GraphQlName))
                {
                    parentTable.MultiLinks.Add(childTable.GraphQlName,
                        new TableLinkDto
                        {
                            Name = childTable.GraphQlName,
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
    }
}
