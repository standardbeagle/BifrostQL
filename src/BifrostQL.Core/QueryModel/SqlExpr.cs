namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Immutable expression tree for column-level SQL expressions (computed columns,
/// projections, filters). Nodes are PURE DATA — they carry no dialect knowledge and
/// emit no SQL themselves. Lowering to dialect-specific, parameterized SQL lives on the
/// dialect layer (<see cref="ISqlDialect.LowerExpression"/> / the Template-Method
/// implementation on <see cref="SqlDialectBase"/>), so every shipped dialect renders the
/// same tree correctly without the tree ever branching on the target engine.
///
/// Every value-bearing node (<see cref="Lit"/>, <see cref="Param"/>) lowers to a bound
/// PARAMETER, never interpolated text; every <see cref="Col"/> is validated against the
/// table's real columns at lowering time. A client-supplied identifier or literal can
/// therefore never reach the SQL string as raw text.
///
/// The node set is a closed hierarchy (nested sealed records): the lowering switch is
/// exhaustive and a new node type is a compile-time obligation, not a silent fall-through.
/// </summary>
public abstract record SqlExpr
{
    private SqlExpr() { }

    /// <summary>A reference to a table column, validated against the table's schema at lowering.</summary>
    public sealed record Col(string Name) : SqlExpr;

    /// <summary>An explicitly bound parameter value with an optional provider DB type.</summary>
    public sealed record Param(object? Value, string? DbType = null) : SqlExpr;

    /// <summary>A literal value. Lowers to a bound parameter (never interpolated) so literal
    /// text never enters the SQL string.</summary>
    public sealed record Lit(object? Value) : SqlExpr;

    /// <summary>A function call over the closed allow-list (UPPER/LOWER/LEN/COALESCE/ABS/ROUND).
    /// An unrecognized name fails fast at lowering — there is no pass-through.</summary>
    public sealed record Fn(string Name, IReadOnlyList<SqlExpr> Args) : SqlExpr;

    /// <summary>A simple CASE: <c>CASE operand WHEN w THEN t ... [ELSE e] END</c>.</summary>
    public sealed record Case(SqlExpr Operand, IReadOnlyList<CaseBranch> Branches, SqlExpr? Else = null) : SqlExpr;

    /// <summary>One WHEN/THEN pair of a <see cref="Case"/>.</summary>
    public sealed record CaseBranch(SqlExpr When, SqlExpr Then);

    /// <summary>A CAST to a portable target type; the concrete storage type is chosen per dialect.</summary>
    public sealed record Cast(SqlExpr Operand, SqlExprType TargetType) : SqlExpr;

    /// <summary>String concatenation of its parts, rendered with the dialect's own concat form
    /// (<c>+</c> / <c>||</c> / <c>CONCAT()</c>) — the node itself does not know which.</summary>
    public sealed record Concat(IReadOnlyList<SqlExpr> Parts) : SqlExpr;
}

/// <summary>
/// Portable CAST target types. Kept deliberately small (only what this slice needs) and
/// mapped to each engine's concrete cast type by <see cref="SqlDialectBase"/> so that no
/// raw type string ever enters the tree — a CAST target is unrepresentable as free text.
/// </summary>
public enum SqlExprType
{
    /// <summary>Textual type (dialect-specific: TEXT / NVARCHAR(MAX) / CHAR).</summary>
    Text,
    /// <summary>Integer type (dialect-specific: INTEGER / INT / SIGNED).</summary>
    Int
}
