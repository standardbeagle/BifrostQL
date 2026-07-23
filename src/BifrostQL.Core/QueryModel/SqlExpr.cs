using System.Text.RegularExpressions;
using BifrostQL.Core.Resolvers;

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

    /// <summary>Adds a signed <paramref name="Amount"/> of <paramref name="Unit"/> to a
    /// temporal <paramref name="Source"/>. There is NO portable spelling (SQL Server
    /// <c>DATEADD</c>, PostgreSQL interval arithmetic, MySQL <c>DATE_ADD</c>, SQLite
    /// <c>datetime()</c>), so each dialect lowers it explicitly — no inherited default.</summary>
    public sealed record DateAdd(SqlExpr Source, DateUnit Unit, SqlExpr Amount) : SqlExpr;

    /// <summary>The whole-<paramref name="Unit"/> difference <c>End - Start</c> between two
    /// temporal expressions. Each dialect lowers it natively; a dialect that cannot express a
    /// given unit exactly (e.g. calendar months/years via epoch math) fails fast rather than
    /// emitting a silently-wrong approximation.</summary>
    public sealed record DateDiff(DateUnit Unit, SqlExpr Start, SqlExpr End) : SqlExpr;

    /// <summary>Extracts a single <paramref name="Unit"/> field (year, month, …) from a temporal
    /// <paramref name="Source"/> as an integer. Lowers per dialect (SQL Server <c>DATEPART</c>,
    /// PostgreSQL/MySQL <c>EXTRACT</c>, SQLite <c>strftime</c>).</summary>
    public sealed record DatePart(DateUnit Unit, SqlExpr Source) : SqlExpr;

    /// <summary>Extracts a scalar value from a JSON <paramref name="Source"/> at a validated
    /// <paramref name="Path"/>. The path is a <see cref="JsonPath"/> of safe segments — never
    /// raw client text — so it cannot inject SQL or JSON-path syntax. Lowers per dialect
    /// (SQL Server <c>JSON_VALUE</c>, PostgreSQL <c>-&gt;&gt;</c>, MySQL
    /// <c>JSON_UNQUOTE(JSON_EXTRACT(...))</c>, SQLite <c>json_extract</c>).</summary>
    public sealed record JsonGet(SqlExpr Source, JsonPath Path) : SqlExpr;
}

/// <summary>
/// The temporal unit shared by <see cref="SqlExpr.DateAdd"/>, <see cref="SqlExpr.DateDiff"/>,
/// and <see cref="SqlExpr.DatePart"/>. Deliberately a closed enum (not a free-text unit string)
/// so a unit can never reach the SQL text as raw input — each dialect maps it to its own keyword.
/// </summary>
public enum DateUnit
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second
}

/// <summary>
/// A validated JSON access path: an ordered list of object-key segments (e.g. <c>user.name</c>).
/// Every segment must be a simple identifier (<c>[A-Za-z_][A-Za-z0-9_]*</c>); anything else —
/// quotes, brackets, dots, dollar signs, whitespace, SQL/JSON-path metacharacters — is rejected
/// at construction. This is the single trust boundary for <see cref="SqlExpr.JsonGet"/>: because
/// a <see cref="JsonGet"/> can only ever hold an already-validated path, each dialect can splice
/// the segments into its native path literal (<c>$.user.name</c> / <c>-&gt;&gt; 'name'</c>)
/// without the segment text ever being able to break out of the string or path grammar.
/// </summary>
public sealed class JsonPath
{
    // Anchored so a segment must be ENTIRELY a safe identifier — a partial match
    // (e.g. "name'); DROP") would still leave the injecting suffix in the segment.
    private static readonly Regex SafeSegment =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>The validated, order-preserving path segments.</summary>
    public IReadOnlyList<string> Segments { get; }

    public JsonPath(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new BifrostExecutionError(
                "A JSON path must have at least one segment; an empty path selects nothing.");

        var copy = new string[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg is null || !SafeSegment.IsMatch(seg))
                throw new BifrostExecutionError(
                    $"Unsafe JSON path segment '{seg}'. A JSON path segment must be a simple identifier " +
                    "([A-Za-z_][A-Za-z0-9_]*) so it cannot inject SQL string or JSON-path syntax. " +
                    "Client-supplied text must be validated into safe segments before building a JsonPath.");
            copy[i] = seg;
        }

        Segments = copy;
    }

    public JsonPath(params string[] segments)
        : this((IReadOnlyList<string>)segments)
    {
    }

    /// <summary>
    /// Renders the segments as a SQLPath/JSONPath literal (<c>$.user.name</c>) for the dialects
    /// that address JSON with a single path string (SQL Server, MySQL, SQLite). Safe to splice
    /// into a single-quoted SQL literal because every segment passed construction validation.
    /// </summary>
    public string ToDollarPath() => "$." + string.Join(".", Segments);
}

/// <summary>
/// Thrown when a specific dialect genuinely cannot lower a given <see cref="SqlExpr"/> node —
/// e.g. a whole-month/year <see cref="SqlExpr.DateDiff"/> on an engine whose only difference
/// primitive is epoch/Julian-day math, which cannot count calendar boundaries exactly. It names
/// BOTH the offending node and the dialect so the failure is actionable, and fails fast instead
/// of emitting a silently-wrong approximation, a NULL, or an empty string. Derives from
/// <see cref="BifrostExecutionError"/> so it travels the same error channel as the rest of
/// expression lowering (unknown column/function), while its type still marks it as a
/// not-supported condition callers can single out.
/// </summary>
public sealed class SqlExprLoweringNotSupportedException : BifrostExecutionError
{
    /// <summary>The unqualified <see cref="SqlExpr"/> node type that could not be lowered.</summary>
    public string NodeType { get; }

    /// <summary>The dialect that cannot lower the node.</summary>
    public string Dialect { get; }

    public SqlExprLoweringNotSupportedException(string nodeType, string dialect, string reason)
        : base($"SQL expression node '{nodeType}' is not supported by the {dialect} dialect: {reason}")
    {
        NodeType = nodeType;
        Dialect = dialect;
    }
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
