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
            foreach (var sourceTable in model.Tables)
            {
                foreach (var link in ParseMetadata(model, sourceTable).Links)
                {
                    AddLink(link);
                    // The target side walks the same bridge the other way round: its field
                    // returns source rows and joins from the target's own key, as AutoDetect
                    // builds it.
                    AddLink(Reverse(link));
                }
            }
        }

        /// <summary>The parse of one table's <c>many-to-many</c> metadata.</summary>
        internal sealed record ParsedMetadata(
            IReadOnlyList<ManyToManyLink> Links, IReadOnlyList<RelationshipMetadataProblem> Problems);

        /// <summary>
        /// The one reading of <paramref name="sourceTable"/>'s <c>many-to-many</c> entries
        /// (<c>Target:Junction</c>, or <c>Target:schema.Junction</c>): discovery links what resolves, and <c>ModelConfigValidator</c>
        /// reports what does not, so a typo fails model load instead of dropping the field.
        /// Negated entries (<c>!Target[:Junction]</c>) prune an auto-detected bridge and declare
        /// nothing here.
        /// </summary>
        internal static ParsedMetadata ParseMetadata(IDbModel model, IDbTable sourceTable)
        {
            var links = new List<ManyToManyLink>();
            var problems = new List<RelationshipMetadataProblem>();
            var m2mValue = sourceTable.GetMetadataValue(MetadataKeys.Relationships.ManyToMany);
            if (string.IsNullOrWhiteSpace(m2mValue))
                return new ParsedMetadata(links, problems);

            const string key = MetadataKeys.Relationships.ManyToMany;
            foreach (var entry in m2mValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (MetadataSwitch.IsNegated(entry))
                    continue;

                var parts = entry.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    problems.Add(new RelationshipMetadataProblem(key, entry,
                        "entries must have the form 'TargetTable:JunctionTable'"));
                    continue;
                }

                var targetTable = model.Tables.FirstOrDefault(t =>
                    string.Equals(t.GraphQlName, parts[0], StringComparison.InvariantCultureIgnoreCase));
                if (targetTable is null)
                {
                    problems.Add(new RelationshipMetadataProblem(key, parts[0], "names no table"));
                    continue;
                }

                if (!TryResolveJunction(model, sourceTable, parts[1], out var junctionTable, out var junctionProblem))
                {
                    problems.Add(new RelationshipMetadataProblem(key, parts[1], junctionProblem));
                    continue;
                }

                if (!TryFindJunctionColumns(sourceTable, junctionTable, targetTable,
                        out var sourceCol, out var junctionSourceCol, out var junctionTargetCol, out var targetCol,
                        out var isComposite))
                {
                    problems.Add(new RelationshipMetadataProblem(key, parts[1],
                        $"the junction table has no foreign key to both {sourceTable.DbName} and {targetTable.DbName}"));
                    continue;
                }

                // Payload = junction columns that are neither a primary key nor one
                // of the two foreign keys to source/target.
                var hasPayload = junctionTable.Columns
                    .Where(c => !c.IsPrimaryKey)
                    .Where(c => !string.Equals(c.ColumnName, junctionSourceCol.ColumnName, StringComparison.OrdinalIgnoreCase))
                    .Where(c => !string.Equals(c.ColumnName, junctionTargetCol.ColumnName, StringComparison.OrdinalIgnoreCase))
                    .Any();

                links.Add(new ManyToManyLink
                {
                    SourceTable = sourceTable,
                    TargetTable = targetTable,
                    JunctionTable = junctionTable,
                    SourceColumn = sourceCol,
                    JunctionSourceColumn = junctionSourceCol,
                    JunctionTargetColumn = junctionTargetCol,
                    TargetColumn = targetCol,
                    HasPayload = hasPayload,
                    // Recorded, not rejected: a composite bridge stays navigable for the
                    // consumers that tolerate a first-column join, while a consumer that
                    // needs the join to be EXACT can refuse it (see ManyToManyLink.IsComposite).
                    IsComposite = isComposite
                });
            }
            return new ParsedMetadata(links, problems);
        }

        /// <summary>
        /// Resolves the junction of a <c>Target:Junction</c> entry. A table's bare name is not its
        /// identity, so <c>schema.name</c> names one table exactly; a bare name resolves in the
        /// source table's schema first, then to the one schema that defines it. A bare name
        /// defined in two or more other schemas is ambiguous: any pick would be read order.
        /// </summary>
        private static bool TryResolveJunction(IDbModel model, IDbTable sourceTable, string name,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDbTable? junction, out string problem)
        {
            problem = "names no junction table";
            var dot = name.IndexOf('.');
            if (dot > 0 && dot < name.Length - 1)
            {
                var schema = name[..dot];
                var dbName = name[(dot + 1)..];
                junction = model.Tables.FirstOrDefault(t =>
                    string.Equals(t.TableSchema, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.DbName, dbName, StringComparison.OrdinalIgnoreCase));
                return junction is not null;
            }

            var candidates = model.Tables
                .Where(t => string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            junction = candidates.FirstOrDefault(t =>
                           string.Equals(t.TableSchema, sourceTable.TableSchema, StringComparison.OrdinalIgnoreCase))
                       ?? (candidates.Count == 1 ? candidates[0] : null);
            if (junction is not null)
                return true;
            if (candidates.Count > 1)
                problem = "is ambiguous: it names " +
                          string.Join(", ", candidates.Select(t => $"{t.TableSchema}.{t.DbName}")) +
                          $", none in {sourceTable.TableSchema}; qualify it as schema.name";
            return false;
        }

        /// <summary>
        /// Adds <paramref name="link"/> to its source under the next free field name, unless the
        /// source already bridges to the same target through the same junction. The key is the
        /// emitted field: a link named after its target would duplicate a same-named column.
        /// </summary>
        private static void AddLink(ManyToManyLink link)
        {
            var source = link.SourceTable;
            if (source.ManyToManyLinks.Values.Any(l =>
                    ReferenceEquals(l.TargetTable, link.TargetTable) && ReferenceEquals(l.JunctionTable, link.JunctionTable)))
                return;
            source.ManyToManyLinks.Add(LinkFieldNames.Unique(source, link.TargetTable.GraphQlName), link);
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

                // A -> junction -> B, then B -> junction -> A. A pair metadata already bridged
                // (or an earlier junction did) keeps that link.
                AddAutoDetectedLink(tableA, junctionTable, tableB,
                    parentColA, junctionColA, junctionColB, parentColB, hasPayload);
                AddAutoDetectedLink(tableB, junctionTable, tableA,
                    parentColB, junctionColB, junctionColA, parentColA, hasPayload);
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
            out ColumnDto junctionTargetCol, out ColumnDto targetCol,
            out bool isComposite)
        {
            sourceCol = null!;
            junctionSourceCol = null!;
            junctionTargetCol = null!;
            targetCol = null!;
            isComposite = false;

            // Resolve by the link's PARENT TABLE rather than by field name. A junction
            // that references the same table twice — the shape of every self-referencing
            // M:N, e.g. friendships(user_a_id, user_b_id) -> users — has no link named
            // after that table at all: each is named for its own FK role. Matching on
            // the table also keeps a self-referencing junction from resolving both
            // sides to one column, which a name lookup did silently.
            var sourceLinks = LinksTo(junctionTable, sourceTable);
            var targetLinks = LinksTo(junctionTable, targetTable);
            if (sourceLinks.Count == 0 || targetLinks.Count == 0)
                return false;

            var sourceLink = sourceLinks[0];
            // Self-referencing junction: the two sides must be DIFFERENT foreign keys.
            var targetLink = ReferenceEquals(sourceTable, targetTable)
                ? targetLinks.FirstOrDefault(l => !ReferenceEquals(l, sourceLink))
                : targetLinks[0];
            if (targetLink is null)
                return false;

            // ChildId / ParentId are the FIRST column of a composite key, so a composite leg
            // is recorded here as a partial join. Report that up rather than hiding it: the
            // link is still built (metadata declared it deliberately), but a consumer whose
            // correctness needs an exact join can refuse it at model load.
            isComposite = sourceLink.IsComposite || targetLink.IsComposite;
            junctionSourceCol = sourceLink.ChildId;
            sourceCol = sourceLink.ParentId;
            junctionTargetCol = targetLink.ChildId;
            targetCol = targetLink.ParentId;
            return true;
        }

        /// <summary>
        /// Every single-link on <paramref name="table"/> whose parent is
        /// <paramref name="parent"/>, in a stable order. There can be more than one when
        /// the table carries several foreign keys to the same target.
        /// </summary>
        private static IReadOnlyList<TableLinkDto> LinksTo(IDbTable table, IDbTable parent) =>
            table.SingleLinks.Values
                .Where(l => string.Equals(l.ParentTable.GraphQlName, parent.GraphQlName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.ParentFieldName, StringComparer.Ordinal)
                .ToArray();

        private static ManyToManyLink Reverse(ManyToManyLink link) => new()
        {
            SourceTable = link.TargetTable,
            TargetTable = link.SourceTable,
            JunctionTable = link.JunctionTable,
            SourceColumn = link.TargetColumn,
            JunctionSourceColumn = link.JunctionTargetColumn,
            JunctionTargetColumn = link.JunctionSourceColumn,
            TargetColumn = link.SourceColumn,
            HasPayload = link.HasPayload,
            IsComposite = link.IsComposite,
        };

        private static void AddAutoDetectedLink(
            IDbTable sourceTable, IDbTable junctionTable, IDbTable targetTable,
            ColumnDto sourceCol, ColumnDto junctionSourceCol,
            ColumnDto junctionTargetCol, ColumnDto targetCol, bool hasPayload)
        {
            if (sourceTable.ManyToManyLinks.Values.Any(l => ReferenceEquals(l.TargetTable, targetTable)))
                return;
            AddLink(new ManyToManyLink
            {
                SourceTable = sourceTable,
                TargetTable = targetTable,
                JunctionTable = junctionTable,
                SourceColumn = sourceCol,
                JunctionSourceColumn = junctionSourceCol,
                JunctionTargetColumn = junctionTargetCol,
                TargetColumn = targetCol,
                HasPayload = hasPayload
            });
        }
    }
}
