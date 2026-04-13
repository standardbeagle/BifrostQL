namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// Detects many-to-many relationships between tables.
    /// Supports both metadata-driven and automatic detection from foreign keys.
    /// </summary>
    public sealed class ManyToManyDetectionStrategy
    {
        /// <summary>
        /// Parses many-to-many metadata on tables. Format: "many-to-many: TargetTable:JunctionTable"
        /// Adds ManyToManyLink entries to both source and target tables.
        /// </summary>
        public void DetectFromMetadata(IDbModel model)
        {
            var tablesByGraphQl = new Dictionary<string, IDbTable>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var table in model.Tables)
            {
                tablesByGraphQl.TryAdd(table.GraphQlName, table);
            }

            foreach (var sourceTable in model.Tables)
            {
                var m2mValue = sourceTable.GetMetadataValue(MetadataKeys.Relationships.ManyToMany);
                if (string.IsNullOrWhiteSpace(m2mValue))
                    continue;

                var entries = m2mValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var entry in entries)
                {
                    var parts = entry.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                        continue;

                    var targetTableName = parts[0];
                    var junctionTableName = parts[1];

                    if (!tablesByGraphQl.TryGetValue(targetTableName, out var targetTable))
                        continue;

                    var junctionTable = model.Tables.FirstOrDefault(t =>
                        string.Equals(t.DbName, junctionTableName, StringComparison.OrdinalIgnoreCase));

                    if (junctionTable == null)
                        continue;

                    // Try to find junction columns via SingleLinks
                    if (!TryFindJunctionColumns(sourceTable, junctionTable, targetTable,
                            out var sourceCol, out var junctionSourceCol, out var junctionTargetCol, out var targetCol))
                        continue;

                    var link = new ManyToManyLink
                    {
                        SourceTable = sourceTable,
                        TargetTable = targetTable,
                        JunctionTable = junctionTable,
                        SourceColumn = sourceCol,
                        JunctionSourceColumn = junctionSourceCol,
                        JunctionTargetColumn = junctionTargetCol,
                        TargetColumn = targetCol
                    };

                    sourceTable.ManyToManyLinks.TryAdd(targetTable.GraphQlName, link);
                    targetTable.ManyToManyLinks.TryAdd(sourceTable.GraphQlName, link);
                }
            }
        }

        /// <summary>
        /// Auto-detects many-to-many relationships by analyzing foreign key patterns.
        /// A junction table has exactly 2 FKs and no additional non-key data columns.
        /// </summary>
        public void AutoDetect(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
        {
            // Group FKs by child table
            var fksByChild = foreignKeys
                .Where(fk => !fk.IsComposite)
                .GroupBy(fk => (fk.ChildTableSchema, fk.ChildTableName))
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (childKey, childFks) in fksByChild)
            {
                // A junction table referencing exactly 2 tables
                if (childFks.Count != 2)
                    continue;

                var junctionTable = model.Tables.FirstOrDefault(t =>
                    string.Equals(t.TableSchema, childKey.ChildTableSchema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.DbName, childKey.ChildTableName, StringComparison.OrdinalIgnoreCase));

                if (junctionTable == null)
                    continue;

                // Check for extra non-key columns (junction tables should only have PK + 2 FKs)
                var nonKeyColumns = junctionTable.Columns
                    .Where(c => !c.IsPrimaryKey)
                    .Where(c => !string.Equals(c.ColumnName, childFks[0].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .Where(c => !string.Equals(c.ColumnName, childFks[1].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (nonKeyColumns.Count > 0)
                    continue;

                // Get the two parent tables
                var parentTables = childFks
                    .Select(fk => model.Tables.FirstOrDefault(t =>
                        string.Equals(t.TableSchema, fk.ParentTableSchema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.DbName, fk.ParentTableName, StringComparison.OrdinalIgnoreCase)))
                    .Where(t => t != null)
                    .ToList();

                if (parentTables.Count != 2)
                    continue;

                var tableA = parentTables[0]!;
                var tableB = parentTables[1]!;

                // Look up columns
                if (!junctionTable.ColumnLookup.TryGetValue(childFks[0].ChildColumnNames[0], out var junctionColA))
                    continue;
                if (!tableA.ColumnLookup.TryGetValue(childFks[0].ParentColumnNames[0], out var parentColA))
                    continue;
                if (!junctionTable.ColumnLookup.TryGetValue(childFks[1].ChildColumnNames[0], out var junctionColB))
                    continue;
                if (!tableB.ColumnLookup.TryGetValue(childFks[1].ParentColumnNames[0], out var parentColB))
                    continue;

                // A -> junction -> B
                if (!tableA.ManyToManyLinks.ContainsKey(tableB.GraphQlName))
                {
                    AddManyToManyLink(tableA, junctionTable, tableB,
                        parentColA, junctionColA, junctionColB, parentColB);
                }

                // B -> junction -> A
                if (!tableB.ManyToManyLinks.ContainsKey(tableA.GraphQlName))
                {
                    AddManyToManyLink(tableB, junctionTable, tableA,
                        parentColB, junctionColB, junctionColA, parentColA);
                }
            }
        }

        private static bool TryFindJunctionColumns(
            IDbTable sourceTable, IDbTable junctionTable, IDbTable targetTable,
            out ColumnDto sourceCol, out ColumnDto junctionSourceCol,
            out ColumnDto junctionTargetCol, out ColumnDto targetCol)
        {
            sourceCol = null!;
            junctionSourceCol = null!;
            junctionTargetCol = null!;
            targetCol = null!;

            // Find junction column referencing source (via SingleLinks on junction)
            if (!junctionTable.SingleLinks.TryGetValue(sourceTable.GraphQlName, out var sourceLink))
                return false;
            // Find junction column referencing target (via SingleLinks on junction)
            if (!junctionTable.SingleLinks.TryGetValue(targetTable.GraphQlName, out var targetLink))
                return false;

            junctionSourceCol = sourceLink.ChildId;
            sourceCol = sourceLink.ParentId;
            junctionTargetCol = targetLink.ChildId;
            targetCol = targetLink.ParentId;
            return true;
        }

        private static void AddManyToManyLink(
            IDbTable sourceTable, IDbTable junctionTable, IDbTable targetTable,
            ColumnDto sourceCol, ColumnDto junctionSourceCol,
            ColumnDto junctionTargetCol, ColumnDto targetCol)
        {
            var link = new ManyToManyLink
            {
                SourceTable = sourceTable,
                TargetTable = targetTable,
                JunctionTable = junctionTable,
                SourceColumn = sourceCol,
                JunctionSourceColumn = junctionSourceCol,
                JunctionTargetColumn = junctionTargetCol,
                TargetColumn = targetCol
            };
            sourceTable.ManyToManyLinks[targetTable.GraphQlName] = link;
        }
    }
}
