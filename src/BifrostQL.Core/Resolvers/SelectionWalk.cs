using GraphQL;
using GraphQLParser.AST;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The one selection-set walk every resolver that derives SQL work from the
    /// request's selection uses. <see cref="IResolveFieldContext.SubFields"/> is keyed
    /// by RESPONSE KEY and holds exactly one node per key, so a schema field selected
    /// twice under the same key (once flat and once through a fragment spread, or
    /// twice flat) loses every node but one — and the dropped node's sub-selection
    /// never reaches the query builder, so its columns serialise as null. Walking the
    /// raw AST here keeps ALL nodes for a schema name.
    /// </summary>
    internal static class SelectionWalk
    {
        /// <summary>
        /// Every AST node selected directly under the resolved field, keyed by the
        /// field's schema name — not its response alias — so detection is
        /// alias-independent. One schema field may appear as several nodes
        /// (<c>s1: _sum {…} s2: _sum {…}</c>, or a flat selection plus a fragment
        /// spread); all of them are kept, because each carries its own sub-selection.
        /// </summary>
        public static IReadOnlyDictionary<string, List<GraphQLField>> SelectedFieldNodes(IResolveFieldContext context)
        {
            var nodes = new Dictionary<string, List<GraphQLField>>(StringComparer.Ordinal);
            Walk(context, context.FieldAst.SelectionSet, f =>
            {
                if (!nodes.TryGetValue(f.Name.StringValue, out var list))
                    nodes[f.Name.StringValue] = list = new List<GraphQLField>();
                list.Add(f);
            });
            return nodes;
        }

        /// <summary>
        /// The distinct schema field names selected under a set of nodes for one
        /// schema field, in first-selection order. Distinct because the same field
        /// selected twice (<c>total: amount amount</c>, or a fragment overlapping a
        /// flat selection) must project one SQL alias — the reader's column index
        /// cannot hold a duplicate. Introspection fields (<c>__typename</c>) are legal
        /// on every object type but are not columns, so they are skipped rather than
        /// rejected.
        /// </summary>
        public static IEnumerable<string> SelectedSubFieldNames(IResolveFieldContext context, IEnumerable<GraphQLField> fields)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
                Walk(context, field.SelectionSet, f =>
                {
                    var name = f.Name.StringValue;
                    if (!name.StartsWith("__", StringComparison.Ordinal) && seen.Add(name))
                        names.Add(name);
                });
            return names;
        }

        /// <summary>
        /// Visits every field node directly under a selection set, descending into
        /// inline fragments and named fragment spreads so fragment-wrapped selections
        /// are seen exactly as flat ones. Unknown fragment names are a validation
        /// error before execution, so an unresolved spread never reaches here.
        /// </summary>
        public static void Walk(IResolveFieldContext context, GraphQLSelectionSet? selectionSet, Action<GraphQLField> visit)
        {
            if (selectionSet == null)
                return;
            foreach (var selection in selectionSet.Selections)
            {
                switch (selection)
                {
                    case GraphQLField f:
                        visit(f);
                        break;
                    case GraphQLInlineFragment inline:
                        Walk(context, inline.SelectionSet, visit);
                        break;
                    case GraphQLFragmentSpread spread:
                        var fragment = context.Document?.Definitions
                            .OfType<GraphQLFragmentDefinition>()
                            .FirstOrDefault(d => d.FragmentName.Name.StringValue == spread.FragmentName.Name.StringValue);
                        if (fragment != null)
                            Walk(context, fragment.SelectionSet, visit);
                        break;
                }
            }
        }
    }
}
