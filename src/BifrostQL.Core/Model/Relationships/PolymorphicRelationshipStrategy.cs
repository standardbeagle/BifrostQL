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
    ///
    /// <para><see cref="Parse"/> is the one reading of the keys: discovery links what it
    /// resolves, and <c>ModelConfigValidator</c> reports what it could not, so a typo fails
    /// model load instead of silently dropping the relationship.</para>
    /// </summary>
    public sealed class PolymorphicRelationshipStrategy
    {
        /// <summary>One resolved <c>polymorphic-map</c> entry: a discriminator value and its parent.</summary>
        internal sealed record Mapping(string TypeValue, IDbTable Parent, ColumnDto ParentKey);

        /// <summary>The parse of one child table's polymorphic keys.</summary>
        internal sealed record Parsed(
            ColumnDto? TypeColumn, ColumnDto? IdColumn, IReadOnlyList<Mapping> Mappings, IReadOnlyList<RelationshipMetadataProblem> Problems)
        {
            public static readonly Parsed None = new(null, null, Array.Empty<Mapping>(), Array.Empty<RelationshipMetadataProblem>());
        }

        public void DiscoverRelationships(IDbModel model)
        {
            foreach (var child in model.Tables)
            {
                var parsed = Parse(model, child);
                if (parsed.TypeColumn is not { } typeCol || parsed.IdColumn is not { } idCol)
                    continue;

                foreach (var mapping in parsed.Mappings)
                {
                    var fieldName = LinkFieldNames.ChildCollection(mapping.Parent, child);
                    mapping.Parent.MultiLinks.Add(fieldName, new TableLinkDto
                    {
                        Name = child.GraphQlName,
                        RelationshipKind = TableLinkRelationshipKind.Polymorphic,
                        ChildFieldNameOverride = fieldName,
                        ParentTable = mapping.Parent,
                        ParentId = mapping.ParentKey,
                        ChildTable = child,
                        ChildId = idCol,
                        TypePredicate = new LinkConstantPredicate(typeCol, mapping.TypeValue),
                    });
                }
            }
        }

        /// <summary>
        /// Reads <paramref name="child"/>'s polymorphic keys. No keys at all is
        /// <see cref="Parsed.None"/>; a partial or unresolvable configuration comes back with
        /// its problems, and only the entries that resolved are mapped.
        /// </summary>
        internal static Parsed Parse(IDbModel model, IDbTable child)
        {
            var typeColName = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicTypeCol);
            var idColName = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicIdCol);
            var mapValue = child.GetMetadataValue(MetadataKeys.Relationships.PolymorphicMap);

            if (string.IsNullOrWhiteSpace(typeColName)
                && string.IsNullOrWhiteSpace(idColName)
                && string.IsNullOrWhiteSpace(mapValue))
                return Parsed.None;

            var problems = new List<RelationshipMetadataProblem>();
            var typeCol = RequireColumn(child, MetadataKeys.Relationships.PolymorphicTypeCol, typeColName, problems);
            var idCol = RequireColumn(child, MetadataKeys.Relationships.PolymorphicIdCol, idColName, problems);
            if (string.IsNullOrWhiteSpace(mapValue))
                problems.Add(new RelationshipMetadataProblem(MetadataKeys.Relationships.PolymorphicMap, string.Empty,
                    "is required with the other polymorphic-* keys; it maps each discriminator value to a parent table"));

            var mappings = new List<Mapping>();
            foreach (var entry in (mapValue ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('=', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    problems.Add(new RelationshipMetadataProblem(MetadataKeys.Relationships.PolymorphicMap, entry,
                        "entries must have the form 'discriminator=parent_table'"));
                    continue;
                }

                if (!TryResolveParent(model, child, parts[1], out var parent))
                {
                    problems.Add(new RelationshipMetadataProblem(MetadataKeys.Relationships.PolymorphicMap, parts[1],
                        "names no table (looked up in the child's schema, then as an unambiguous name)"));
                    continue;
                }

                // A discriminator id column holds one key value, so the parent needs exactly one
                // primary-key column: zero-key and composite-key parents cannot be joined.
                var keys = parent.KeyColumns.ToList();
                if (keys.Count != 1)
                {
                    problems.Add(new RelationshipMetadataProblem(MetadataKeys.Relationships.PolymorphicMap, parts[1],
                        "the parent must have a single-column primary key for the id column to reference"));
                    continue;
                }

                mappings.Add(new Mapping(parts[0], parent, keys[0]));
            }

            return new Parsed(typeCol, idCol, mappings, problems);
        }

        private static ColumnDto? RequireColumn(IDbTable child, string key, string? name, List<RelationshipMetadataProblem> problems)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new RelationshipMetadataProblem(key, string.Empty, "is required with the other polymorphic-* keys"));
                return null;
            }
            if (child.ColumnLookup.TryGetValue(name, out var column))
                return column;
            problems.Add(new RelationshipMetadataProblem(key, name, "names a column that does not exist on the table"));
            return null;
        }

        // The child's own schema first (two schemas may each hold a 'post'), then a name that
        // is unambiguous across the model.
        private static bool TryResolveParent(IDbModel model, IDbTable child, string dbName, out IDbTable parent)
        {
            parent = model.Tables.FirstOrDefault(t =>
                         string.Equals(t.TableSchema, child.TableSchema, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(t.DbName, dbName, StringComparison.OrdinalIgnoreCase))!;
            if (parent is not null)
                return true;
            if (model.TryGetTableFromDbName(dbName, out var unique))
            {
                parent = unique;
                return true;
            }
            return false;
        }
    }
}
