using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server
{
    /// <summary>
    /// Renders the shared <see cref="SchemaReadVisibility"/> projection — the single funnel every
    /// introspection surface shares, calling the SAME evaluator the query path enforces — onto the
    /// app-metadata overlay, so the served overlay describes only what the caller may actually
    /// READ. The authorization decision, and its fail-closed handling of an unparseable policy,
    /// live in Core; only the overlay reshaping is here.
    ///
    /// <para>The overlay is an INTROSPECTION surface: it enumerates entities, their fields, their
    /// grid columns and their relationships. Served unfiltered it discloses the shape of relations
    /// a caller cannot query — the exact information-disclosure side channel invariant 4
    /// (.claude/rules/protocol-adapter-security.md) forbids.</para>
    ///
    /// <para><b>Fail closed.</b> An entity whose table is absent from the model, or whose policy
    /// cannot be evaluated, is EXCLUDED — never included on a benefit-of-the-doubt basis. A field
    /// naming a column that does not exist, or one the caller may not read, is dropped, and every
    /// column reference that survives (display fields, grid columns/filters/sort, relationship
    /// foreign-key fields) is filtered against the same set. A relationship whose target entity is
    /// not visible is omitted, so the overlay never advertises an unreachable endpoint.</para>
    ///
    /// <para>Workflows are carried through unchanged: they are deployment-authored definitions
    /// with no table-keyed shape to filter here. Gating them is a separate concern from this
    /// entity/field projection.</para>
    /// </summary>
    internal static class AppMetadataVisibility
    {
        /// <summary>
        /// Returns the overlay restricted to the entities and fields <paramref name="userContext"/>
        /// may read under <paramref name="model"/>.
        /// </summary>
        public static AppMetadataModel Project(
            AppMetadataModel overlay, IDbModel model, IDictionary<string, object?> userContext)
        {
            if (overlay is null) throw new ArgumentNullException(nameof(overlay));

            var visible = SchemaReadVisibility.Project(model, userContext);

            // First pass: which overlay keys resolve to a table this caller may read. Relationship
            // targets are filtered against this set, so a link to a hidden entity never surfaces.
            var readable = new Dictionary<string, VisibleTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in overlay.Entities)
            {
                if (FindTable(visible, key) is { } table)
                    readable[key] = table;
            }

            var entities = new Dictionary<string, EntityMetadata>(overlay.Entities.Count, StringComparer.Ordinal);
            foreach (var (key, entity) in overlay.Entities)
            {
                if (!readable.TryGetValue(key, out var table))
                    continue;

                entities[key] = ProjectEntity(entity, table, readable.Keys);
            }

            return overlay with { Entities = entities };
        }

        private static EntityMetadata ProjectEntity(
            EntityMetadata entity, VisibleTable table, IEnumerable<string> visibleEntityKeys)
        {
            var visibleEntities = new HashSet<string>(visibleEntityKeys, StringComparer.OrdinalIgnoreCase);

            var fields = entity.Fields
                .Where(kv => table.HasColumn(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            var relationships = entity.Relationships
                .Where(kv => kv.Value.TargetEntity is { } target && visibleEntities.Contains(target))
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value with
                    {
                        // A relationship's own column references are columns of the TARGET entity's
                        // display; the FK field is a column of this entity. Both are filtered so a
                        // read-denied column is not named by a surviving relationship.
                        ForeignKeyField = kv.Value.ForeignKeyField is { } fk && table.HasColumn(fk)
                            ? fk
                            : null,
                    },
                    StringComparer.Ordinal);

            return entity with
            {
                DisplayFields = Filter(entity.DisplayFields, table),
                Fields = fields,
                Grid = entity.Grid is null ? null : entity.Grid with
                {
                    DefaultColumns = Filter(entity.Grid.DefaultColumns, table),
                    DefaultFilters = Filter(entity.Grid.DefaultFilters, table),
                    DefaultSort = Filter(entity.Grid.DefaultSort, table),
                },
                Relationships = relationships,
            };
        }

        private static IReadOnlyList<string> Filter(IReadOnlyList<string> names, VisibleTable table)
            => names.Count == 0 ? names : names.Where(table.HasColumn).ToList();

        /// <summary>
        /// Resolves an overlay key (a qualified table name, e.g. <c>dbo.users</c>) to the caller's
        /// projection of that table. Returns null when the model has no such table, and equally
        /// when the caller may not read it — an overlay entry describing something this deployment
        /// does not expose, or this caller may not see, is dropped rather than published.
        /// </summary>
        private static VisibleTable? FindTable(IReadOnlyList<VisibleTable> visible, string key)
            => visible.FirstOrDefault(v =>
                string.Equals($"{v.Table.TableSchema}.{v.Table.DbName}", key, StringComparison.OrdinalIgnoreCase));
    }
}
