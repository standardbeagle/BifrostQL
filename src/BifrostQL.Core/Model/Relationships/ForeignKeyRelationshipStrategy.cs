namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Discovers table relationships from database foreign key constraints.
    /// Creates single-links (child -> parent) and multi-links (parent -> children).
    ///
    /// <para>Foreign keys are grouped by the (child, parent) pair before any link is
    /// named, because the name depends on how many times that pair occurs. A child
    /// that references a parent ONCE gets a field named for the parent table — the
    /// common case, and the historical behaviour. A child that references the same
    /// parent MORE than once (orders -> addresses for both billing and shipping) gets
    /// one field per foreign key, each named for its own FK column, and the bare
    /// parent name is not emitted at all: it can only ever describe one of the keys,
    /// so keeping it would leave a field whose meaning depends on the order the
    /// driver enumerated constraints in.</para>
    /// </summary>
    public sealed class ForeignKeyRelationshipStrategy : ITableRelationshipStrategy
    {
        /// <inheritdoc />
        public void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            var tablesByDbName = model.Tables
                .ToDictionary(t => (Schema: t.TableSchema, Name: t.DbName),
                    t => t,
                    new SchemaTableComparer());

            var resolved = new List<ResolvedForeignKey>(foreignKeys.Count);
            foreach (var fk in foreignKeys)
            {
                if (TryResolve(fk, tablesByDbName, out var resolvedFk))
                    resolved.Add(resolvedFk);
            }

            // Group by the (child, parent) pair, and order within each group by the
            // constraint name so the field names a schema produces never depend on
            // the order the driver handed the constraints back.
            var groups = resolved
                .GroupBy(r => (Child: r.ChildTable.GraphQlName, Parent: r.ParentTable.GraphQlName));

            foreach (var group in groups)
            {
                var links = group.OrderBy(r => r.ConstraintName, StringComparer.Ordinal).ToArray();
                var needsRoleNames = links.Length > 1;

                foreach (var fk in links)
                {
                    // The field shares the child's namespace with its columns: a FK column
                    // named after its parent (invoice.customer -> customer) takes the bare
                    // name, so the link gets the next free one.
                    var parentFieldName = needsRoleNames
                        ? UniqueParentFieldName(fk)
                        : LinkFieldNames.Unique(fk.ChildTable, fk.ParentTable.GraphQlName);

                    // ChildId/ParentId remain the first column for back-compat; the
                    // full ordered lists power multi-column ON-clauses in SQL emission.
                    fk.ChildTable.SingleLinks.Add(parentFieldName,
                        new TableLinkDto
                        {
                            Name = fk.ParentTable.GraphQlName,
                            RelationshipKind = TableLinkRelationshipKind.ForeignKey,
                            ParentFieldNameOverride =
                                string.Equals(parentFieldName, fk.ParentTable.GraphQlName, StringComparison.Ordinal)
                                    ? null
                                    : parentFieldName,
                            ChildId = fk.ChildColumns[0],
                            ParentId = fk.ParentColumns[0],
                            ChildIds = fk.ChildColumns,
                            ParentIds = fk.ParentColumns,
                            ChildTable = fk.ChildTable,
                            ParentTable = fk.ParentTable
                        });

                    // The parent side needs the same treatment: with two FKs an address
                    // has two distinct collections of orders (the ones billed to it and
                    // the ones shipped to it), and naming both after the child table
                    // would merge them.
                    var childFieldName = needsRoleNames
                        ? UniqueChildRoleFieldName(fk, parentFieldName)
                        : LinkFieldNames.ChildCollection(fk.ParentTable, fk.ChildTable);

                    fk.ParentTable.MultiLinks.Add(childFieldName,
                        new TableLinkDto
                        {
                            Name = fk.ChildTable.GraphQlName,
                            RelationshipKind = TableLinkRelationshipKind.ForeignKey,
                            ChildFieldNameOverride = childFieldName,
                            ChildId = fk.ChildColumns[0],
                            ParentId = fk.ParentColumns[0],
                            ChildIds = fk.ChildColumns,
                            ParentIds = fk.ParentColumns,
                            ChildTable = fk.ChildTable,
                            ParentTable = fk.ParentTable
                        });
                }
            }
        }

        private static bool TryResolve(
            DbForeignKey fk,
            IReadOnlyDictionary<(string Schema, string Name), IDbTable> tablesByDbName,
            out ResolvedForeignKey resolved)
        {
            resolved = default!;
            if (!tablesByDbName.TryGetValue((fk.ChildTableSchema, fk.ChildTableName), out var childTable))
                return false;
            if (!tablesByDbName.TryGetValue((fk.ParentTableSchema, fk.ParentTableName), out var parentTable))
                return false;

            // Resolve every column on both sides; if any column is unknown to the
            // loaded model, skip the whole FK so we never produce a half-formed link.
            var childColumns = new List<ColumnDto>(fk.ChildColumnNames.Count);
            var parentColumns = new List<ColumnDto>(fk.ParentColumnNames.Count);
            for (var i = 0; i < fk.ChildColumnNames.Count; i++)
            {
                if (!childTable.ColumnLookup.TryGetValue(fk.ChildColumnNames[i], out var childCol)
                    || !parentTable.ColumnLookup.TryGetValue(fk.ParentColumnNames[i], out var parentCol))
                    return false;
                childColumns.Add(childCol);
                parentColumns.Add(parentCol);
            }

            resolved = new ResolvedForeignKey(
                fk.ConstraintName ?? string.Empty, childTable, parentTable, childColumns, parentColumns);
            return true;
        }

        internal static string ResolveChildFieldNameForTest(IDbTable parentTable, IDbTable childTable) =>
            LinkFieldNames.ChildCollection(parentTable, childTable);

        /// <summary>
        /// Names a child -> parent field after the foreign key's own columns, e.g.
        /// <c>billing_address_id</c> -> <c>billing_address</c>. The link field shares a
        /// namespace with the table's columns, so a stem that collides with a real
        /// column (or another link) falls back to the parent name with a suffix.
        /// </summary>
        private static string UniqueParentFieldName(ResolvedForeignKey fk)
        {
            var stem = string.Join("_", fk.ChildColumns.Select(c => StripKeySuffix(c.GraphQlName)))
                .Trim('_');
            var baseName = stem.Length > 0 ? stem : fk.ParentTable.GraphQlName;

            return LinkFieldNames.Unique(fk.ChildTable, baseName);
        }

        /// <summary>
        /// Names a parent -> children collection for the role its foreign key plays,
        /// e.g. addresses gets <c>orders_by_billing_address</c> and
        /// <c>orders_by_shipping_address</c> rather than two fields both called orders.
        /// </summary>
        private static string UniqueChildRoleFieldName(ResolvedForeignKey fk, string role)
            => LinkFieldNames.Unique(fk.ParentTable, $"{fk.ChildTable.GraphQlName}_by_{role}");

        /// <summary>Trims the trailing key marker from a FK column name (<c>customer_id</c> -> <c>customer</c>).</summary>
        private static string StripKeySuffix(string columnName)
        {
            foreach (var suffix in new[] { "_id", "_key", "id" })
            {
                if (columnName.Length > suffix.Length
                    && columnName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return columnName[..^suffix.Length].TrimEnd('_');
            }
            return columnName;
        }

        private sealed record ResolvedForeignKey(
            string ConstraintName,
            IDbTable ChildTable,
            IDbTable ParentTable,
            IReadOnlyList<ColumnDto> ChildColumns,
            IReadOnlyList<ColumnDto> ParentColumns);
    }
}
