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
    /// Renders the shared <see cref="SchemaReadVisibility"/> projection — the single funnel every
    /// introspection surface shares, calling the SAME evaluator the query path enforces — into the
    /// OData entity/navigation shape the service document and <c>$metadata</c> are built from. The
    /// authorization decision, and its fail-closed handling of an unparseable policy, live in
    /// Core; only the entity/navigation modelling is OData's
    /// (.claude/rules/protocol-adapter-security.md invariant 4).
    /// </summary>
    internal static class ODataModelVisibility
    {
        /// <summary>
        /// Returns the visible entity types/sets for <paramref name="userContext"/>. A table
        /// whose Read is denied — or whose policy cannot be evaluated — is omitted entirely, as
        /// is any navigation whose far end is not fully visible.
        /// </summary>
        public static IReadOnlyList<ODataEntity> Project(
            IDbModel model, IDictionary<string, object?> userContext)
        {
            var visible = SchemaReadVisibility.Project(model, userContext);

            var result = new List<ODataEntity>(visible.Count);
            foreach (var entry in visible)
            {
                result.Add(new ODataEntity(
                    entry.Table,
                    entry.Columns,
                    // Every key column the caller may read is emitted (composite keys represented
                    // in full), never a first-column guess.
                    entry.KeyColumns,
                    VisibleNavigations(entry.Table, visible)));
            }

            return result;
        }

        /// <summary>
        /// Builds navigation properties for the table's foreign-key links the caller may see.
        /// Single links (many-to-one) become single-valued navigations; multi links (one-to-many)
        /// become collection-valued ones. Many-to-many links (through a hidden junction table) are
        /// an unsupported shape here and are deterministically OMITTED rather than reduced to a
        /// single-column guess. Each navigation name is the link's own key in the table's link
        /// dictionary, which is unique per table.
        ///
        /// <para>An edge survives only when <see cref="SchemaReadVisibility.IsLinkVisible"/> holds
        /// — both end tables AND every participating key column visible — so the metadata never
        /// advertises an unreachable endpoint, nor names a column the data path would refuse.</para>
        /// </summary>
        private static IReadOnlyList<ODataNavigation> VisibleNavigations(
            IDbTable table, IReadOnlyList<VisibleTable> visible)
        {
            var result = new List<ODataNavigation>();
            var takenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, link) in table.SingleLinks)
            {
                if (link?.ParentTable is not { } target || !SchemaReadVisibility.IsLinkVisible(link, visible))
                    continue;
                if (takenNames.Add(name))
                    result.Add(new ODataNavigation(name, target.GraphQlName, IsCollection: false));
            }

            foreach (var (name, link) in table.MultiLinks)
            {
                if (link?.ChildTable is not { } target || !SchemaReadVisibility.IsLinkVisible(link, visible))
                    continue;
                if (takenNames.Add(name))
                    result.Add(new ODataNavigation(name, target.GraphQlName, IsCollection: true));
            }

            return result;
        }
    }
}
