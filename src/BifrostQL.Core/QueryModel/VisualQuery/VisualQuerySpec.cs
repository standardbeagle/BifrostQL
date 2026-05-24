namespace BifrostQL.Core.QueryModel.VisualQuery
{
    /// <summary>
    /// Serializable contract for the Access-style visual query builder. The React
    /// designer assembles a <see cref="VisualQuerySpec"/> and ships it over the
    /// Photino bridge; the server-side builder turns it into a parameterized
    /// SELECT via <c>ISqlDialect</c>. This file is the single source of truth for
    /// the shape — the TypeScript mirror lives in
    /// <c>src/BifrostQL.UI/frontend/src/lib/visual-query.ts</c> and must stay in
    /// lockstep.
    ///
    /// Enum-like fields (Sort, join Type, filter Op, criterion Operator) are
    /// plain strings rather than CLR enums on purpose: the bridge serializes with
    /// <c>System.Text.Json</c> using camelCase property names but no string-enum
    /// converter, and the TS side models these as string-literal unions. Strings
    /// round-trip identically across both ends with zero converter wiring. Allowed
    /// values are published as constants (<see cref="VisualSort"/>,
    /// <see cref="VisualJoinType"/>, <see cref="VisualFilterOp"/>,
    /// <see cref="VisualFilterOperator"/>) for the builder to validate against.
    ///
    /// This task delivers types only — no SQL generation logic.
    /// </summary>
    public sealed record VisualQuerySpec(
        IReadOnlyList<VisualTable> Tables,
        IReadOnlyList<VisualColumn> Columns,
        IReadOnlyList<VisualJoin> Joins,
        VisualFilter? Filter,
        int? RowLimit);

    /// <summary>A table placed on the design surface. <paramref name="Table"/> is
    /// the qualified "schema.name". <paramref name="Alias"/> disambiguates the same
    /// table added twice (self-join).</summary>
    public sealed record VisualTable(
        string Table,
        string? Alias);

    /// <summary>A selected/criteria column. <paramref name="Show"/> controls
    /// whether it appears in the SELECT list (a column can participate in sorting
    /// or filtering without being shown, exactly like the Access grid).</summary>
    public sealed record VisualColumn(
        string Table,
        string Column,
        string? Alias,
        bool Show,
        /// <summary>One of <see cref="VisualSort"/>.</summary>
        string Sort,
        /// <summary>Ordinal among sorted columns; lower sorts first. Null when not sorted.</summary>
        int? SortOrder);

    /// <summary>A join between two tables. Column lists are parallel arrays so a
    /// composite foreign key joins on multiple column pairs (LeftColumns[i] =
    /// RightColumns[i]).</summary>
    public sealed record VisualJoin(
        string LeftTable,
        IReadOnlyList<string> LeftColumns,
        string RightTable,
        IReadOnlyList<string> RightColumns,
        /// <summary>One of <see cref="VisualJoinType"/>.</summary>
        string Type);

    /// <summary>A node in the filter tree. A node is either a group
    /// (<see cref="VisualFilterOp.And"/>/<see cref="VisualFilterOp.Or"/> with
    /// <paramref name="Children"/>) or a leaf (<see cref="VisualFilterOp.Leaf"/>
    /// with a <paramref name="Criterion"/>). The unused arm is null.</summary>
    public sealed record VisualFilter(
        /// <summary>One of <see cref="VisualFilterOp"/>.</summary>
        string Op,
        IReadOnlyList<VisualFilter>? Children,
        VisualCriterion? Criterion);

    /// <summary>A single comparison. <paramref name="Operator"/> reuses the
    /// existing BifrostQL filter operators (see <see cref="VisualFilterOperator"/>).
    /// <paramref name="Value"/> is an arbitrary JSON value: scalar for most
    /// operators, an array for <c>_in</c>/<c>_between</c>, ignored for
    /// <c>_null</c>.</summary>
    public sealed record VisualCriterion(
        string Table,
        string Column,
        string Operator,
        object? Value);

    /// <summary>Allowed <see cref="VisualColumn.Sort"/> values.</summary>
    public static class VisualSort
    {
        public const string None = "none";
        public const string Asc = "asc";
        public const string Desc = "desc";
    }

    /// <summary>Allowed <see cref="VisualJoin.Type"/> values.</summary>
    public static class VisualJoinType
    {
        public const string Inner = "inner";
        public const string Left = "left";
    }

    /// <summary>Allowed <see cref="VisualFilter.Op"/> values.</summary>
    public static class VisualFilterOp
    {
        public const string And = "and";
        public const string Or = "or";
        public const string Leaf = "leaf";
    }

    /// <summary>Allowed <see cref="VisualCriterion.Operator"/> values, matching the
    /// GraphQL filter operators.</summary>
    public static class VisualFilterOperator
    {
        public const string Eq = "_eq";
        public const string Neq = "_neq";
        public const string Lt = "_lt";
        public const string Lte = "_lte";
        public const string Gt = "_gt";
        public const string Gte = "_gte";
        public const string Contains = "_contains";
        public const string In = "_in";
        public const string Between = "_between";
        public const string Null = "_null";
    }
}
