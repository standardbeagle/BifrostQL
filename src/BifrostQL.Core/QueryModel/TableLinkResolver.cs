using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>Which kind of relationship a link field on a table names.</summary>
    public enum ResolvedLinkKind
    {
        None,
        /// <summary>Child to parent: a bare object (<see cref="TableLinkDto.ParentTable"/>).</summary>
        Single,
        /// <summary>Parent to children: a paged collection (<see cref="TableLinkDto.ChildTable"/>).</summary>
        Multi,
        /// <summary>Source to target through a junction: a paged collection.</summary>
        ManyToMany,
    }

    /// <summary>
    /// The relationship a link field resolves to on one table. Exactly one of
    /// <see cref="Link"/> / <see cref="ManyToMany"/> is set unless <see cref="Kind"/>
    /// is <see cref="ResolvedLinkKind.None"/>.
    /// </summary>
    public readonly record struct ResolvedLink(ResolvedLinkKind Kind, TableLinkDto? Link, ManyToManyLink? ManyToMany)
    {
        public static readonly ResolvedLink None = new(ResolvedLinkKind.None, null, null);

        public bool Found => Kind != ResolvedLinkKind.None;

        /// <summary>The table the field navigates to, or null for <see cref="ResolvedLinkKind.None"/>.</summary>
        public IDbTable? TargetTable => Kind switch
        {
            ResolvedLinkKind.Single => Link!.ParentTable,
            ResolvedLinkKind.Multi => Link!.ChildTable,
            ResolvedLinkKind.ManyToMany => ManyToMany!.TargetTable,
            _ => null,
        };
    }

    /// <summary>
    /// The one answer to "which relationship does this field name on this table".
    /// Query building (<see cref="QueryField"/>), link connection
    /// (<see cref="GqlObjectQuery.ConnectLinks"/>) and aggregate link paths all
    /// resolve through here, so they cannot disagree on a field's meaning.
    /// </summary>
    public static class TableLinkResolver
    {
        /// <summary>
        /// Resolves <paramref name="fieldName"/> against the links of
        /// <paramref name="table"/>. Precedence: a single link wins over a multi
        /// link (a self-referential FK exposes both under related names), and a
        /// multi link wins over a many-to-many collection. Each kind matches by its
        /// dictionary key or by the GraphQL field name the schema advertises for it.
        /// </summary>
        public static ResolvedLink TryResolveLink(IDbTable table, string fieldName)
        {
            if (table.SingleLinks.TryGetValue(fieldName, out var singleLink)
                || (singleLink = table.SingleLinks.Values.FirstOrDefault(l => string.Equals(l.ParentFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                return new ResolvedLink(ResolvedLinkKind.Single, singleLink, null);

            if (table.MultiLinks.TryGetValue(fieldName, out var multiLink)
                || (multiLink = table.MultiLinks.Values.FirstOrDefault(l => string.Equals(l.ChildFieldName, fieldName, StringComparison.OrdinalIgnoreCase))) != null)
                return new ResolvedLink(ResolvedLinkKind.Multi, multiLink, null);

            // A many-to-many link's key is its emitted field name.
            if (table.ManyToManyLinks.TryGetValue(fieldName, out var manyToMany))
                return new ResolvedLink(ResolvedLinkKind.ManyToMany, null, manyToMany);

            return ResolvedLink.None;
        }
    }
}
