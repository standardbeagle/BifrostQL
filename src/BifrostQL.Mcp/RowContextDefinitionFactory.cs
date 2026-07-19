using BifrostQL.Core.Model;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Synthesizes a <see cref="DeclarativeToolDefinition"/> for the generic
    /// <c>bifrost_row_context</c> tool from a table's schema, so the tool's query is
    /// built by the SAME declarative pipeline
    /// (<see cref="DeclarativeQueryToolCompiler"/>) that compiles author-declared
    /// tools — not a parallel hand-written query builder. The two layers therefore
    /// cannot drift: a change to how a by-id query (root row, resolved parents, child
    /// summaries) is compiled changes both at once.
    ///
    /// <para>The row-context tool still SHAPES its own response envelope (parents with
    /// found/displayName, children with totalCount + first rows) from the compiled
    /// query results — the dogfooding is about query CONSTRUCTION, not output shape.</para>
    /// </summary>
    internal static class RowContextDefinitionFactory
    {
        internal const string IdParameterName = "id";
        internal const int ChildSummaryRowCount = 5;
        private const int ParentRowLimit = 1;

        /// <summary>An outgoing foreign key resolved to a parent summary include.</summary>
        internal sealed record ParentRelation(
            string As,
            string RelationName,
            IDbTable Table,
            IReadOnlyList<ColumnDto> ForeignKeyColumns,
            ColumnDto? DisplayColumn);

        /// <summary>An incoming foreign key resolved to a child-collection summary include.</summary>
        internal sealed record ChildRelation(string As, string RelationName, IDbTable Table);

        internal sealed record Synthesized(
            DeclarativeToolDefinition Definition,
            IReadOnlyList<ParentRelation> Parents,
            IReadOnlyList<ChildRelation> Children);

        /// <summary>
        /// Builds the declarative definition plus the parent/child descriptors the tool
        /// uses to reshape the compiled results into the row-context envelope.
        /// </summary>
        internal static Synthesized Build(IDbTable table)
        {
            var qualifiedName = $"{table.TableSchema}.{table.DbName}";
            var includes = new List<DeclarativeToolInclude>();
            var parents = new List<ParentRelation>();
            var children = new List<ChildRelation>();

            // Outgoing FKs → parent summaries: address each parent by its key and
            // resolve a display name. One include per relation, matched by the FULL
            // ordered FK column list inside the compiler (never index-zero).
            foreach (var (relation, link) in OrderedSingleLinks(table))
            {
                var parentTable = link.ParentTable;
                var displayColumn = SchemaDescriber.DisplayColumn(parentTable);
                var fields = new List<ColumnDto>(parentTable.KeyColumns);
                if (displayColumn is not null && fields.All(c => !SameColumn(c, displayColumn)))
                    fields.Add(displayColumn);
                var asName = "parent__" + relation;
                includes.Add(new DeclarativeToolInclude
                {
                    Relation = relation,
                    As = asName,
                    Fields = fields.Select(c => c.GraphQlName).ToArray(),
                    Limit = ParentRowLimit,
                });
                parents.Add(new ParentRelation(
                    asName, relation, parentTable, link.ChildIds.ToArray(), displayColumn));
            }

            // Incoming FKs → child-collection summaries: count + first rows, ordered by
            // the child's first key column for a deterministic top-N.
            foreach (var (relation, link) in OrderedMultiLinks(table))
            {
                var childTable = link.ChildTable;
                var asName = "child__" + relation;
                includes.Add(new DeclarativeToolInclude
                {
                    Relation = relation,
                    As = asName,
                    Fields = QueryToolCompiler.SummaryColumns(childTable).Select(c => c.GraphQlName).ToArray(),
                    Limit = ChildSummaryRowCount,
                    Sort = childTable.KeyColumns.FirstOrDefault()?.GraphQlName,
                });
                children.Add(new ChildRelation(asName, relation, childTable));
            }

            var definition = new DeclarativeToolDefinition
            {
                Name = DataTools.RowContextToolName,
                Description = "Generic row context (row + resolved parents + child summaries).",
                Params = new Dictionary<string, DeclarativeToolParameter>
                {
                    [IdParameterName] = new() { Type = "id" },
                },
                Root = new DeclarativeToolRoot
                {
                    Table = qualifiedName,
                    ById = IdParameterName,
                    Fields = table.Columns.OrderBy(c => c.OrdinalPosition).Select(c => c.GraphQlName).ToArray(),
                },
                Include = includes,
            };

            return new Synthesized(definition, parents, children);
        }

        private static IEnumerable<(string Relation, TableLinkDto Link)> OrderedSingleLinks(IDbTable table) =>
            table.SingleLinks
                .Where(kvp => SameTable(kvp.Value.ChildTable, table))
                .OrderBy(kvp => kvp.Value.ParentTable.DbName, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => (kvp.Key, kvp.Value));

        private static IEnumerable<(string Relation, TableLinkDto Link)> OrderedMultiLinks(IDbTable table) =>
            table.MultiLinks
                .Where(kvp => SameTable(kvp.Value.ParentTable, table))
                .OrderBy(kvp => kvp.Value.ChildTable.DbName, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => (kvp.Key, kvp.Value));

        private static bool SameTable(IDbTable a, IDbTable b) =>
            string.Equals(a.DbName, b.DbName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.TableSchema, b.TableSchema, StringComparison.OrdinalIgnoreCase);

        private static bool SameColumn(ColumnDto a, ColumnDto b) =>
            string.Equals(a.DbName, b.DbName, StringComparison.OrdinalIgnoreCase);
    }
}
