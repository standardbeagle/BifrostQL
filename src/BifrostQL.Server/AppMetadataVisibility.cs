using BifrostQL.Core.AppMetadata;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server
{
    /// <summary>
    /// Projects the app-metadata overlay down to what a given caller may actually READ, using the
    /// SAME authoritative check the query path enforces — <see cref="PolicyEvaluator"/> over the
    /// per-table <see cref="TablePolicy"/> that <see cref="PolicyConfigCollector"/> parses, with
    /// identity reconstructed by the shared <see cref="PolicyIdentity"/> projection.
    ///
    /// <para>The overlay is an INTROSPECTION surface: it enumerates entities, their fields, their
    /// grid columns and their relationships. Served unfiltered it discloses the shape of relations
    /// a caller cannot query — the exact information-disclosure side channel invariant 4
    /// (.claude/rules/protocol-adapter-security.md) forbids. This is deliberately not a second,
    /// weaker "it's just presentation metadata" rule; it calls the evaluator the data path calls,
    /// mirroring <c>PgCatalogVisibility</c>, <c>ODataModelVisibility</c> and
    /// <c>GrpcSchemaVisibility</c>.</para>
    ///
    /// <para><b>Fail closed.</b> An entity whose table is absent from the model, or whose policy
    /// cannot be evaluated, is EXCLUDED — never included on a benefit-of-the-doubt basis. A field
    /// naming a column that does not exist, or one the caller may not read, is dropped, and every
    /// column reference that survives (display fields, grid columns/filters/sort, relationship
    /// display columns) is filtered against the same set. A relationship whose target entity is
    /// not visible is omitted, so the overlay never advertises an unreachable endpoint.</para>
    ///
    /// <para>Workflows are carried through unchanged: they are deployment-authored definitions
    /// with no table-keyed shape to filter here. Gating them is a separate concern from this
    /// entity/field projection.</para>
    /// </summary>
    internal static class AppMetadataVisibility
    {
        private static readonly PolicyEvaluator Evaluator = new();

        /// <summary>
        /// Returns the overlay restricted to the entities and fields <paramref name="userContext"/>
        /// may read under <paramref name="model"/>.
        /// </summary>
        public static AppMetadataModel Project(
            AppMetadataModel overlay, IDbModel model, IDictionary<string, object?> userContext)
        {
            if (overlay is null) throw new ArgumentNullException(nameof(overlay));
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var identity = PolicyIdentity.FromUserContext(userContext);

            // First pass: which overlay keys resolve to a table this caller may read. Relationship
            // targets are filtered against this set, so a link to a hidden entity never surfaces.
            var readable = new Dictionary<string, IDbTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, _) in overlay.Entities)
            {
                var table = FindTable(model, key);
                if (table is not null && CanRead(table, identity))
                    readable[key] = table;
            }

            var entities = new Dictionary<string, EntityMetadata>(overlay.Entities.Count, StringComparer.Ordinal);
            foreach (var (key, entity) in overlay.Entities)
            {
                if (!readable.TryGetValue(key, out var table))
                    continue;

                var visibleColumns = VisibleColumns(table, identity);
                entities[key] = ProjectEntity(entity, visibleColumns, readable.Keys);
            }

            return overlay with { Entities = entities };
        }

        private static EntityMetadata ProjectEntity(
            EntityMetadata entity, HashSet<string> visibleColumns, IEnumerable<string> visibleEntityKeys)
        {
            var visibleEntities = new HashSet<string>(visibleEntityKeys, StringComparer.OrdinalIgnoreCase);

            var fields = entity.Fields
                .Where(kv => visibleColumns.Contains(kv.Key))
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
                        ForeignKeyField = kv.Value.ForeignKeyField is { } fk && visibleColumns.Contains(fk)
                            ? fk
                            : null,
                    },
                    StringComparer.Ordinal);

            return entity with
            {
                DisplayFields = Filter(entity.DisplayFields, visibleColumns),
                Fields = fields,
                Grid = entity.Grid is null ? null : entity.Grid with
                {
                    DefaultColumns = Filter(entity.Grid.DefaultColumns, visibleColumns),
                    DefaultFilters = Filter(entity.Grid.DefaultFilters, visibleColumns),
                    DefaultSort = Filter(entity.Grid.DefaultSort, visibleColumns),
                },
                Relationships = relationships,
            };
        }

        private static IReadOnlyList<string> Filter(IReadOnlyList<string> names, HashSet<string> allowed)
            => names.Count == 0 ? names : names.Where(allowed.Contains).ToList();

        /// <summary>
        /// Resolves an overlay key (a qualified table name, e.g. <c>dbo.users</c>) to its table.
        /// Returns null when the model has no such table — an overlay entry describing something
        /// this deployment does not expose is dropped rather than published unverified.
        /// </summary>
        private static IDbTable? FindTable(IDbModel model, string key)
            => model.Tables.FirstOrDefault(t =>
                string.Equals($"{t.TableSchema}.{t.DbName}", key, StringComparison.OrdinalIgnoreCase));

        private static bool CanRead(IDbTable table, AppIdentity identity)
        {
            try
            {
                return Evaluator.CanAct(PolicyConfigCollector.FromTable(table), PolicyAction.Read, identity).Allowed;
            }
            catch
            {
                // Fail closed: a table whose policy cannot be parsed/evaluated is hidden.
                return false;
            }
        }

        /// <summary>
        /// The column names of <paramref name="table"/> the caller may read, matched
        /// case-insensitively so an overlay authored in either casing lines up with the model.
        /// </summary>
        private static HashSet<string> VisibleColumns(IDbTable table, AppIdentity identity)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            TablePolicy policy;
            try
            {
                policy = PolicyConfigCollector.FromTable(table);
            }
            catch
            {
                // Survived CanRead but the policy no longer parses: treat as no visible columns.
                return result;
            }

            foreach (var column in table.Columns)
            {
                bool allowed;
                try
                {
                    allowed = Evaluator.IsColumnAllowed(policy, column.DbName, PolicyDirection.Read, identity).Allowed;
                }
                catch
                {
                    allowed = false; // fail closed on any column-evaluation fault
                }

                if (allowed)
                {
                    result.Add(column.DbName);
                    result.Add(column.GraphQlName);
                }
            }

            return result;
        }
    }
}
