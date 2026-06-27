using BifrostQL.Core.Utils;

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
                    // A negated entry (e.g. "!Roles:UserRoles") is a suppression, not a
                    // declaration — it prunes an auto-detected link (handled in AutoDetect
                    // via IsM2MSuppressed), so there is nothing to add here.
                    if (MetadataSwitch.IsNegated(entry))
                        continue;

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

                    // Payload = junction columns that are neither a primary key nor one
                    // of the two foreign keys to source/target.
                    var hasPayload = junctionTable.Columns
                        .Where(c => !c.IsPrimaryKey)
                        .Where(c => !string.Equals(c.ColumnName, junctionSourceCol.ColumnName, StringComparison.OrdinalIgnoreCase))
                        .Where(c => !string.Equals(c.ColumnName, junctionTargetCol.ColumnName, StringComparison.OrdinalIgnoreCase))
                        .Any();

                    var link = new ManyToManyLink
                    {
                        SourceTable = sourceTable,
                        TargetTable = targetTable,
                        JunctionTable = junctionTable,
                        SourceColumn = sourceCol,
                        JunctionSourceColumn = junctionSourceCol,
                        JunctionTargetColumn = junctionTargetCol,
                        TargetColumn = targetCol,
                        HasPayload = hasPayload
                    };

                    sourceTable.ManyToManyLinks.TryAdd(targetTable.GraphQlName, link);
                    targetTable.ManyToManyLinks.TryAdd(sourceTable.GraphQlName, link);
                }
            }
        }

        /// <summary>
        /// Auto-detects many-to-many relationships by analyzing foreign key patterns.
        /// A junction table must have exactly 2 (non-composite) FKs, NO extra
        /// non-key data columns, AND a junction signal: either its name references
        /// both linked tables (the conventional <c>tableA_tableB</c> link-table
        /// naming) OR its primary key is composed of exactly those two FK columns
        /// (the textbook composite-key bridge, e.g. <c>enrollments(student_id,
        /// course_id)</c> whose name references neither endpoint). The guards keep
        /// ordinary entities that merely carry two FKs (e.g. sessions ->
        /// session_entries -> participants, which has a surrogate PK and/or its own
        /// data columns) from being mistaken for a pure bridge. Any guard can be
        /// overridden with explicit <c>many-to-many:</c> metadata (see
        /// <see cref="DetectFromMetadata"/>).
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

                // Real extra columns (non-key, non-FK) mean this is a first-class
                // entity carrying its own data, not a pure link table. Auto-detection
                // bows out — the schema author can still opt in via metadata.
                var nonKeyColumns = junctionTable.Columns
                    .Where(c => !c.IsPrimaryKey)
                    .Where(c => !string.Equals(c.ColumnName, childFks[0].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .Where(c => !string.Equals(c.ColumnName, childFks[1].ChildColumnNames[0], StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (nonKeyColumns.Count > 0)
                    continue;

                // Auto-detected junctions are always pure (the payload guard above
                // already bailed out otherwise).
                const bool hasPayload = false;

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

                // Require a junction signal: either the link-table naming
                // convention (e.g. `user_roles`, `StudentCourses`) OR a primary key
                // composed of exactly the two FK columns (e.g. `enrollments`). The
                // PK-shape signal catches semantically-named bridges the naming
                // convention misses; a surrogate-PK entity satisfies neither and is
                // left alone, so a coincidental two-FK entity is not mis-bridged.
                if (!JunctionNameReferencesBoth(junctionTable, tableA, tableB)
                    && !JunctionPkIsExactlyForeignKeys(junctionTable, childFks[0], childFks[1]))
                    continue;

                // Look up columns
                if (!junctionTable.ColumnLookup.TryGetValue(childFks[0].ChildColumnNames[0], out var junctionColA))
                    continue;
                if (!tableA.ColumnLookup.TryGetValue(childFks[0].ParentColumnNames[0], out var parentColA))
                    continue;
                if (!junctionTable.ColumnLookup.TryGetValue(childFks[1].ChildColumnNames[0], out var junctionColB))
                    continue;
                if (!tableB.ColumnLookup.TryGetValue(childFks[1].ParentColumnNames[0], out var parentColB))
                    continue;

                // A negated many-to-many entry on EITHER endpoint prunes the whole
                // bridge — a single "!Roles" on Users kills Users<->Roles in both
                // directions.
                if (IsM2MSuppressed(tableA, tableB) || IsM2MSuppressed(tableB, tableA))
                    continue;

                // A -> junction -> B
                if (!tableA.ManyToManyLinks.ContainsKey(tableB.GraphQlName))
                {
                    AddManyToManyLink(tableA, junctionTable, tableB,
                        parentColA, junctionColA, junctionColB, parentColB, hasPayload);
                }

                // B -> junction -> A
                if (!tableB.ManyToManyLinks.ContainsKey(tableA.GraphQlName))
                {
                    AddManyToManyLink(tableB, junctionTable, tableA,
                        parentColB, junctionColB, junctionColA, parentColA, hasPayload);
                }
            }
        }

        /// <summary>
        /// True when <paramref name="source"/> carries a negated many-to-many
        /// metadata entry (e.g. <c>many-to-many: !Target</c> or
        /// <c>many-to-many: !Target:Junction</c>) naming <paramref name="target"/>.
        /// This lets a schema author prune one auto-detected bridge while leaving
        /// the wide auto-detection net in place for every other pair.
        /// </summary>
        private static bool IsM2MSuppressed(IDbTable source, IDbTable target)
        {
            var raw = source.GetMetadataValue(MetadataKeys.Relationships.ManyToMany);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!MetadataSwitch.IsNegated(entry))
                    continue;

                // Bare form is "Target" or "Target:Junction" — match on the target.
                var targetName = MetadataSwitch.StripNegation(entry)
                    .Split(':', StringSplitOptions.TrimEntries)[0];
                if (string.Equals(targetName, target.GraphQlName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetName, target.DbName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the junction table name references BOTH linked tables — the
        /// conventional <c>tableA_tableB</c> link-table naming. Tolerates
        /// singular/plural drift (Users &lt;-&gt; user). A self-referencing junction
        /// (both endpoints the same table) needs that one name present.
        /// </summary>
        private static bool JunctionNameReferencesBoth(IDbTable junction, IDbTable a, IDbTable b)
        {
            var junctionName = StringNormalizer.NormalizeName(junction.DbName);
            return NameReferencesTable(junctionName, a.DbName)
                && NameReferencesTable(junctionName, b.DbName);
        }

        /// <summary>
        /// True when the junction's primary key is composed of exactly the two FK
        /// child columns and nothing else — the canonical composite-key bridge
        /// (e.g. <c>enrollments(student_id, course_id)</c>). A surrogate-PK entity
        /// (PK = <c>Id</c>) fails this, keeping ordinary two-FK entities from being
        /// mistaken for a pure junction.
        /// </summary>
        private static bool JunctionPkIsExactlyForeignKeys(IDbTable junction, DbForeignKey fkA, DbForeignKey fkB)
        {
            var pkColumns = junction.Columns
                .Where(c => c.IsPrimaryKey)
                .Select(c => c.ColumnName)
                .ToList();
            if (pkColumns.Count != 2)
                return false;

            // The two FKs are non-composite (filtered upstream), so each carries a
            // single child column.
            var fkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fkA.ChildColumnNames[0],
                fkB.ChildColumnNames[0],
            };
            // Two distinct FK columns that together cover the entire PK.
            return fkColumns.Count == 2 && pkColumns.All(fkColumns.Contains);
        }

        private static bool NameReferencesTable(string junctionName, string tableName)
        {
            var name = StringNormalizer.NormalizeName(tableName);
            if (name.Length == 0)
                return false;
            if (junctionName.Contains(name, StringComparison.Ordinal))
                return true;
            // Tolerate singular/plural drift between the table name and how it
            // appears inside the junction name (roles -> role, sessions -> session).
            var singular = name.EndsWith("s", StringComparison.Ordinal) ? name[..^1] : name;
            return singular.Length > 0 && junctionName.Contains(singular, StringComparison.Ordinal);
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
            ColumnDto junctionTargetCol, ColumnDto targetCol, bool hasPayload)
        {
            var link = new ManyToManyLink
            {
                SourceTable = sourceTable,
                TargetTable = targetTable,
                JunctionTable = junctionTable,
                SourceColumn = sourceCol,
                JunctionSourceColumn = junctionSourceCol,
                JunctionTargetColumn = junctionTargetCol,
                TargetColumn = targetCol,
                HasPayload = hasPayload
            };
            sourceTable.ManyToManyLinks[targetTable.GraphQlName] = link;
        }
    }
}
