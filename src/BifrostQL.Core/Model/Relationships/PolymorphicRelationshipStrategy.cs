namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Surfaces a polymorphic child table — one that references several parent
    /// tables through a discriminator column plus a shared id column (e.g. a
    /// <c>notes</c> table with <c>entity_type</c>/<c>entity_id</c>) — as a
    /// distinct navigable child collection on each referenced parent.
    ///
    /// Driven entirely by table metadata (<see cref="MetadataKeys.Relationships.PolymorphicTypeCol"/>,
    /// <see cref="MetadataKeys.Relationships.PolymorphicIdCol"/>,
    /// <see cref="MetadataKeys.Relationships.PolymorphicMap"/>); it does not require
    /// foreign keys, so it works even where the database declares none (e.g. SQLite).
    ///
    /// Each mapped parent receives a <see cref="TableLinkDto"/> in its
    /// <c>MultiLinks</c> carrying a <see cref="LinkConstantPredicate"/> on the
    /// discriminator column. The constant is applied at query time as an extra
    /// filter on the child node (see GqlObjectQuery.ConnectLinks), keeping each
    /// parent's collection isolated to its own rows.
    /// </summary>
    public sealed class PolymorphicRelationshipStrategy
    {
        public void DiscoverRelationships(IDbModel model)
        {
            var tablesByDbName = new Dictionary<string, IDbTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in model.Tables)
                tablesByDbName.TryAdd(table.DbName, table);

            foreach (var child in model.Tables)
            {
                var typeColName = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicTypeCol);
                var idColName = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicIdCol);
                var mapValue = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicMap);

                if (string.IsNullOrWhiteSpace(typeColName)
                    || string.IsNullOrWhiteSpace(idColName)
                    || string.IsNullOrWhiteSpace(mapValue))
                    continue;

                if (!child.ColumnLookup.TryGetValue(typeColName, out var typeCol))
                    continue;
                if (!child.ColumnLookup.TryGetValue(idColName, out var idCol))
                    continue;

                foreach (var entry in mapValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = entry.Split('=', StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                        continue;

                    var typeValue = parts[0];
                    var parentDbName = parts[1];
                    if (string.IsNullOrWhiteSpace(typeValue) || string.IsNullOrWhiteSpace(parentDbName))
                        continue;

                    if (!tablesByDbName.TryGetValue(parentDbName, out var parent))
                        continue;

                    // Require exactly one primary key: a discriminator id column
                    // can only reference a single parent key value, so both
                    // zero-key tables (no PK at all) and composite-key tables
                    // (Count > 1) are unsupported and must be skipped here to
                    // avoid a crash on the KeyColumns.First() call below.
                    if (parent.KeyColumns.Count() != 1)
                        continue;

                    var parentKey = parent.KeyColumns.First();
                    var fieldName = UniqueChildFieldName(parent, child);
                    if (parent.MultiLinks.ContainsKey(fieldName))
                        continue;

                    parent.MultiLinks.Add(fieldName, new TableLinkDto
                    {
                        Name = child.GraphQlName,
                        ChildFieldNameOverride = fieldName,
                        ParentTable = parent,
                        ParentId = parentKey,
                        ChildTable = child,
                        ChildId = idCol,
                        TypePredicate = new LinkConstantPredicate(typeCol, typeValue),
                    });
                }
            }
        }

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
