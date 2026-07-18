using BifrostQL.Core.Model;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// One planned <c>$expand</c> navigation: the schema relationship to follow and the two
    /// single-column key columns that bind a parent row to its expanded target rows. Every field is
    /// schema-derived — the navigation, its cardinality, and BOTH key columns come from the model's
    /// relationship link (<see cref="IDbTable.SingleLinks"/>/<see cref="IDbTable.MultiLinks"/>),
    /// never guessed from a request string and never a first-column slice of a composite key
    /// (a composite relationship is rejected before a plan item is ever built —
    /// .claude/rules/composite-pk-compliance.md).
    /// </summary>
    /// <param name="Name">The navigation property name to emit on the response (schema-derived).</param>
    /// <param name="IsCollection">True for a to-many (collection) navigation, false for to-one.</param>
    /// <param name="RootKeyColumn">
    /// The column whose value is present on each ROOT row and identifies which target rows belong to
    /// it: the FK column for a to-one link (root is the child), the PK column for a to-many link
    /// (root is the parent).
    /// </param>
    /// <param name="TargetKeyColumn">
    /// The column on the TARGET entity the expand filters and groups by: the PK for a to-one link,
    /// the FK for a to-many link. The mirror of <paramref name="RootKeyColumn"/>.
    /// </param>
    /// <param name="Target">The visible target entity whose rows are expanded (its own read scope).</param>
    internal sealed record ODataExpandItem(
        string Name,
        bool IsCollection,
        ColumnDto RootKeyColumn,
        ColumnDto TargetKeyColumn,
        ODataEntity Target);

    /// <summary>
    /// Parses and plans the OData v4 <c>$expand</c> option into a set of one-level, schema-validated
    /// navigations. This slice supports exactly ONE level of expansion of a declared schema
    /// relationship; anything richer is rejected with a deterministic OData 400 rather than silently
    /// served:
    /// <list type="bullet">
    /// <item>a nested option (<c>nav($select=…)</c> / <c>nav($expand=…)</c>) — the parenthesised
    /// form — is unsupported (no nested $filter/$orderby/$expand inside an expand);</item>
    /// <item>a multi-level path (<c>a/b</c>) is a second level and is rejected;</item>
    /// <item>an unknown navigation (not a visible relationship on the entity) is rejected — the same
    /// fail-closed visibility the read path enforces, so an expand can never reach a table the
    /// caller may not read;</item>
    /// <item>a self-referential navigation (target entity == source entity) is rejected as cyclic —
    /// the one-level bound is enforced structurally, never by recursing a relationship graph
    /// (.claude/rules/protocol-adapter-security.md invariant 6);</item>
    /// <item>a composite-key relationship is rejected as an explicitly unsupported shape — the
    /// adapter never falls back to the first key column of a multi-column FK
    /// (.claude/rules/composite-pk-compliance.md).</item>
    /// </list>
    /// The plan carries only schema-derived, visibility-checked column references; the actual reads
    /// are executed as independent scoped intents by <see cref="ODataExpandExecutor"/>.
    /// </summary>
    internal static class ODataExpand
    {
        private const string Context = "$expand";

        /// <summary>
        /// Splits the raw <c>$expand</c> text into its comma-separated navigation names, rejecting
        /// the syntactic shapes this slice does not support before any schema lookup. Returns the
        /// list of navigation name tokens (never null; empty text is no expand and is handled by the
        /// caller). A duplicate navigation, an empty item, a nested-option (parenthesised) item, or a
        /// multi-level path item is a clean OData 400.
        /// </summary>
        public static IReadOnlyList<string> Parse(string? expandText)
        {
            if (string.IsNullOrWhiteSpace(expandText))
                return Array.Empty<string>();

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in expandText.Split(','))
            {
                var item = raw.Trim();
                if (item.Length == 0)
                    throw ODataProtocolException.BadRequest($"{Context} contains an empty navigation.");

                // Nested options: nav($select=…), nav($expand=…), nav($filter=…). Rejected wholesale —
                // this slice serves no option inside an expand.
                if (item.IndexOf('(') >= 0 || item.IndexOf(')') >= 0)
                    throw ODataProtocolException.BadRequest(
                        $"{Context} does not support nested options on '{item}'.");

                // A path (a/b) is a second expansion level, which this one-level slice rejects.
                if (item.IndexOf('/') >= 0)
                    throw ODataProtocolException.BadRequest(
                        $"{Context} does not support multi-level navigation '{item}'.");

                if (!seen.Add(item))
                    throw ODataProtocolException.BadRequest(
                        $"{Context} names navigation '{item}' more than once.");

                names.Add(item);
            }

            return names;
        }

        /// <summary>
        /// Resolves each parsed navigation name against <paramref name="entity"/>'s VISIBLE
        /// navigations and the caller's visible entity set, producing a schema-derived plan. Unknown,
        /// self-referential (cyclic), and composite-key navigations are rejected with a 400; the two
        /// binding key columns must themselves be visible (readable) or the navigation is likewise
        /// rejected — an expand never selects a column the caller cannot read.
        /// </summary>
        public static IReadOnlyList<ODataExpandItem> Plan(
            ODataEntity entity, IReadOnlyList<string> navigationNames, IReadOnlyList<ODataEntity> visibleEntities)
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            if (navigationNames is null) throw new ArgumentNullException(nameof(navigationNames));
            if (visibleEntities is null) throw new ArgumentNullException(nameof(visibleEntities));

            if (navigationNames.Count == 0)
                return Array.Empty<ODataExpandItem>();

            var items = new List<ODataExpandItem>(navigationNames.Count);
            foreach (var name in navigationNames)
                items.Add(PlanOne(entity, name, visibleEntities));
            return items;
        }

        private static ODataExpandItem PlanOne(
            ODataEntity entity, string name, IReadOnlyList<ODataEntity> visibleEntities)
        {
            var navMatches = entity.Navigations
                .Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (navMatches.Count == 0)
                throw ODataProtocolException.BadRequest(
                    $"{Context} references unknown navigation '{name}' on entity '{entity.Table.GraphQlName}'.");
            if (navMatches.Count > 1)
                throw ODataProtocolException.BadRequest(
                    $"{Context} references ambiguous navigation '{name}' on entity '{entity.Table.GraphQlName}'.");
            var nav = navMatches[0];

            // One-level bound enforced structurally: a navigation back to the same entity is the
            // start of a cycle and is rejected outright rather than recursed.
            if (string.Equals(nav.TargetEntity, entity.Table.GraphQlName, StringComparison.Ordinal))
                throw ODataProtocolException.BadRequest(
                    $"{Context} does not support the self-referential navigation '{name}'.");

            var target = visibleEntities.FirstOrDefault(
                e => string.Equals(e.Table.GraphQlName, nav.TargetEntity, StringComparison.Ordinal));
            if (target is null)
                // Defensive: VisibleNavigations already filters to visible targets, so this only
                // fires on an inconsistent projection — treat as unknown, never leak the target.
                throw ODataProtocolException.BadRequest(
                    $"{Context} references unknown navigation '{name}' on entity '{entity.Table.GraphQlName}'.");

            // Resolve the underlying schema relationship link. The navigation name IS the link's own
            // dictionary key (ODataModelVisibility builds navigations from those keys), so the lookup
            // is exact. to-one -> SingleLink (root is the child), to-many -> MultiLink (root is the parent).
            var (rootKeyCol, targetKeyCol) = nav.IsCollection
                ? ResolveMultiLink(entity, target, nav.Name)
                : ResolveSingleLink(entity, target, nav.Name);

            return new ODataExpandItem(nav.Name, nav.IsCollection, rootKeyCol, targetKeyCol, target);
        }

        /// <summary>
        /// A to-one navigation: the root table is the child, the target is the parent. The root-side
        /// key is the FK column (<see cref="TableLinkDto.ChildId"/>); the target-side key is the
        /// parent PK (<see cref="TableLinkDto.ParentId"/>). A composite FK is rejected — never a
        /// first-column slice.
        /// </summary>
        private static (ColumnDto Root, ColumnDto Target) ResolveSingleLink(
            ODataEntity entity, ODataEntity target, string navName)
        {
            if (!entity.Table.SingleLinks.TryGetValue(navName, out var link))
                throw ODataProtocolException.BadRequest(
                    $"{Context} references unknown navigation '{navName}' on entity '{entity.Table.GraphQlName}'.");

            RejectComposite(link, navName);
            var rootKey = RequireVisible(entity, link.ChildId, navName);
            var targetKey = RequireVisible(target, link.ParentId, navName);
            return (rootKey, targetKey);
        }

        /// <summary>
        /// A to-many navigation: the root table is the parent, the target is the child. The root-side
        /// key is the parent PK (<see cref="TableLinkDto.ParentId"/>); the target-side key is the FK
        /// column (<see cref="TableLinkDto.ChildId"/>). A composite FK is rejected — never a
        /// first-column slice.
        /// </summary>
        private static (ColumnDto Root, ColumnDto Target) ResolveMultiLink(
            ODataEntity entity, ODataEntity target, string navName)
        {
            if (!entity.Table.MultiLinks.TryGetValue(navName, out var link))
                throw ODataProtocolException.BadRequest(
                    $"{Context} references unknown navigation '{navName}' on entity '{entity.Table.GraphQlName}'.");

            RejectComposite(link, navName);
            var rootKey = RequireVisible(entity, link.ParentId, navName);
            var targetKey = RequireVisible(target, link.ChildId, navName);
            return (rootKey, targetKey);
        }

        /// <summary>
        /// A composite (multi-column) relationship is an explicitly unsupported shape for this
        /// slice's $expand: correctly mapping it needs an AND of per-column equalities that the
        /// single-key intent binding here does not build, and the one thing that is never acceptable
        /// is silently binding only the first column pair. Reject deterministically instead
        /// (.claude/rules/composite-pk-compliance.md — surface an explicit "unsupported
        /// relationship", never a `column[0]` guess).
        /// </summary>
        private static void RejectComposite(TableLinkDto link, string navName)
        {
            if (link.IsComposite)
                throw ODataProtocolException.BadRequest(
                    $"{Context} does not support the composite-key navigation '{navName}'.");
        }

        /// <summary>
        /// Confirms the binding key column is itself readable by the caller (present in the entity's
        /// identity-filtered column projection). If it is read-denied the navigation cannot be
        /// expanded without either selecting a hidden column or guessing — so it is rejected, never
        /// served with a bypassed key.
        /// </summary>
        private static ColumnDto RequireVisible(ODataEntity entity, ColumnDto keyColumn, string navName)
        {
            var visible = entity.Columns.FirstOrDefault(
                c => string.Equals(c.DbName, keyColumn.DbName, StringComparison.OrdinalIgnoreCase));
            if (visible is null)
                throw ODataProtocolException.BadRequest(
                    $"{Context} cannot expand '{navName}': its key column is not readable.");
            return visible;
        }
    }
}
