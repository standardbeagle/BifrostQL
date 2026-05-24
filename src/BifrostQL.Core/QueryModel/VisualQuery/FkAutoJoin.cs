using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.QueryModel.VisualQuery
{
    /// <summary>
    /// Derives candidate joins between two tables from the database model's foreign
    /// key metadata, so the designer can auto-wire an obvious join when a related
    /// table is dropped onto the canvas.
    ///
    /// Returns every FK path between the pair (in both directions), composite-FK
    /// aware via <see cref="TableLinkDto.ChildIds"/>/<see cref="TableLinkDto.ParentIds"/>:
    /// <list type="bullet">
    ///   <item><description>0 candidates — no relationship; the UI falls back to a manual join.</description></item>
    ///   <item><description>1 candidate — unambiguous; the UI applies it automatically.</description></item>
    ///   <item><description>2+ candidates — ambiguous; the UI asks the user to pick (never guess silently).</description></item>
    /// </list>
    /// Each candidate defaults to <see cref="VisualJoinType.Inner"/>; the join editor
    /// can switch it to LEFT.
    ///
    /// Limitation: candidates come from the model's per-parent <c>SingleLinks</c>
    /// map, so two distinct FKs from the same child to the same parent collapse to
    /// one candidate. Ambiguity is therefore detected across directions (A→B and
    /// B→A) but not for multiple same-parent FKs — the raw FK list isn't exposed
    /// on <see cref="IDbModel"/>.
    /// </summary>
    public static class FkAutoJoin
    {
        public static IReadOnlyList<VisualJoin> Resolve(IDbModel model, string tableRefA, string tableRefB)
        {
            ArgumentNullException.ThrowIfNull(model);

            var a = ResolveTable(model, tableRefA);
            var b = ResolveTable(model, tableRefB);

            var candidates = new List<VisualJoin>();

            // A's child->parent FKs that point at B (A references B).
            foreach (var link in a.SingleLinks.Values)
                if (ReferenceEquals(link.ParentTable, b) || SameTable(link.ParentTable, b))
                    candidates.Add(ToJoin(link));

            // B's child->parent FKs that point at A (B references A).
            foreach (var link in b.SingleLinks.Values)
                if (ReferenceEquals(link.ParentTable, a) || SameTable(link.ParentTable, a))
                    candidates.Add(ToJoin(link));

            return candidates;
        }

        private static VisualJoin ToJoin(TableLinkDto link) => new(
            LeftTable: Qualified(link.ChildTable),
            LeftColumns: link.ChildIds.Select(c => c.DbName).ToList(),
            RightTable: Qualified(link.ParentTable),
            RightColumns: link.ParentIds.Select(c => c.DbName).ToList(),
            Type: VisualJoinType.Inner);

        private static bool SameTable(IDbTable x, IDbTable y) =>
            string.Equals(x.TableSchema, y.TableSchema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DbName, y.DbName, StringComparison.OrdinalIgnoreCase);

        private static string Qualified(IDbTable t) =>
            string.IsNullOrEmpty(t.TableSchema) ? t.DbName : $"{t.TableSchema}.{t.DbName}";

        private static IDbTable ResolveTable(IDbModel model, string qualified)
        {
            if (string.IsNullOrWhiteSpace(qualified))
                throw new BifrostExecutionError("Table name must not be empty.");

            string? schema = null;
            var name = qualified;
            var dot = qualified.IndexOf('.');
            if (dot >= 0)
            {
                schema = qualified[..dot];
                name = qualified[(dot + 1)..];
            }

            var matches = model.Tables.Where(t =>
                string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase)
                && (schema is null || string.Equals(t.TableSchema, schema, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
                throw new BifrostExecutionError($"Table '{qualified}' was not found in the database model.");
            if (matches.Count > 1)
                throw new BifrostExecutionError($"Table '{qualified}' is ambiguous; qualify it with a schema.");

            return matches[0];
        }
    }
}
