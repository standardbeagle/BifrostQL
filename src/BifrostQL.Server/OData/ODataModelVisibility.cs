using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// A single OData entity type/set projected from an <see cref="IDbTable"/> for a given
    /// identity: the table, the columns the caller may READ, the subset of those that form the
    /// key, and the navigation endpoints whose target entity is itself visible to the caller.
    /// </summary>
    internal sealed record ODataEntity(
        IDbTable Table,
        IReadOnlyList<ColumnDto> Columns,
        IReadOnlyList<ColumnDto> KeyColumns,
        IReadOnlyList<ODataNavigation> Navigations);

    /// <summary>A navigation property to another visible entity type.</summary>
    /// <param name="Name">The navigation property name (schema-derived, from the link).</param>
    /// <param name="TargetEntity">The GraphQL name of the target table (its EntityType name).</param>
    /// <param name="IsCollection">True for a one-to-many (collection) navigation.</param>
    internal sealed record ODataNavigation(string Name, string TargetEntity, bool IsCollection);

    /// <summary>
    /// Projects an <see cref="IDbModel"/> to the entity types/sets a given identity may READ,
    /// using the SAME authoritative check the query path enforces —
    /// <see cref="PolicyEvaluator"/> over the per-table <see cref="TablePolicy"/> that
    /// <see cref="PolicyConfigCollector"/> parses, with identity reconstructed by the shared
    /// <see cref="PolicyIdentity"/> projection. This is deliberately NOT a second, weaker
    /// "it's just metadata" rule: a table the caller could not read is never listed in the
    /// service document or <c>$metadata</c>, or the introspection surface would leak the
    /// existence of relations the identity cannot query
    /// (.claude/rules/protocol-adapter-security.md invariant 4). Mirrors the pgwire catalog
    /// visibility filter (<c>PgCatalogVisibility</c>) — the same Core seam, a different wire.
    ///
    /// <para><b>Fail closed.</b> A table whose policy cannot be evaluated (malformed policy
    /// metadata, any evaluation fault) is EXCLUDED, never included on a benefit-of-the-doubt
    /// basis — even for admin, since <see cref="PolicyConfigCollector.FromTable"/> throws before
    /// the evaluator runs. Column read-deny is applied identically, so a read-denied column is
    /// absent from every emitted type. A navigation whose target table is not visible to the
    /// caller is omitted, so the metadata never advertises an unreachable/unauthorized
    /// endpoint.</para>
    /// </summary>
    internal static class ODataModelVisibility
    {
        private static readonly PolicyEvaluator Evaluator = new();

        /// <summary>
        /// Returns the visible entity types/sets for <paramref name="userContext"/>. A table
        /// whose Read is denied — or whose policy cannot be evaluated — is omitted entirely, as
        /// is any navigation to such a table.
        /// </summary>
        public static IReadOnlyList<ODataEntity> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var identity = PolicyIdentity.FromUserContext(userContext);

            // First pass: the set of tables the caller may read. Navigation endpoints are
            // filtered against this set so a link to a hidden/denied table never surfaces.
            var visibleTables = new List<IDbTable>();
            foreach (var table in model.Tables)
            {
                if (CanRead(table, identity))
                    visibleTables.Add(table);
            }

            var visibleNames = new HashSet<string>(
                visibleTables.Select(t => t.GraphQlName), StringComparer.Ordinal);

            var result = new List<ODataEntity>(visibleTables.Count);
            foreach (var table in visibleTables)
            {
                var columns = VisibleColumns(table, identity);
                var visibleColumnNames = new HashSet<string>(
                    columns.Select(c => c.DbName), StringComparer.OrdinalIgnoreCase);

                // Key columns are the visible subset of the table's key — every key column is
                // emitted (composite keys represented in full), never a first-column guess.
                var keyColumns = table.KeyColumns
                    .Where(c => visibleColumnNames.Contains(c.DbName))
                    .ToList();

                var navigations = VisibleNavigations(table, visibleNames);
                result.Add(new ODataEntity(table, columns, keyColumns, navigations));
            }

            return result;
        }

        private static bool CanRead(IDbTable table, AppIdentity identity)
        {
            try
            {
                var policy = PolicyConfigCollector.FromTable(table);
                return Evaluator.CanAct(policy, PolicyAction.Read, identity).Allowed;
            }
            catch
            {
                // Fail closed: a table whose policy cannot be parsed/evaluated is hidden.
                return false;
            }
        }

        private static IReadOnlyList<ColumnDto> VisibleColumns(IDbTable table, AppIdentity identity)
        {
            TablePolicy policy;
            try
            {
                policy = PolicyConfigCollector.FromTable(table);
            }
            catch
            {
                // Survived CanRead but the policy no longer parses: treat as no visible columns.
                return Array.Empty<ColumnDto>();
            }

            var result = new List<ColumnDto>();
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
                    result.Add(column);
            }

            return result;
        }

        /// <summary>
        /// Builds navigation properties for the table's foreign-key links whose target entity is
        /// itself visible. Single links (many-to-one) become single-valued navigations; multi
        /// links (one-to-many) become collection-valued ones. Many-to-many links (through a
        /// hidden junction table) are an unsupported shape here and are deterministically OMITTED
        /// rather than reduced to a single-column guess. Each navigation name is the link's own
        /// key in the table's link dictionary, which is unique per table.
        /// </summary>
        private static IReadOnlyList<ODataNavigation> VisibleNavigations(
            IDbTable table, HashSet<string> visibleTableNames)
        {
            var result = new List<ODataNavigation>();
            var takenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, link) in table.SingleLinks)
            {
                var target = link.ParentTable;
                if (target is null || !visibleTableNames.Contains(target.GraphQlName))
                    continue;
                if (takenNames.Add(name))
                    result.Add(new ODataNavigation(name, target.GraphQlName, IsCollection: false));
            }

            foreach (var (name, link) in table.MultiLinks)
            {
                var target = link.ChildTable;
                if (target is null || !visibleTableNames.Contains(target.GraphQlName))
                    continue;
                if (takenNames.Add(name))
                    result.Add(new ODataNavigation(name, target.GraphQlName, IsCollection: true));
            }

            return result;
        }
    }
}
