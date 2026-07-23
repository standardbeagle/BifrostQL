using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Thrown when the public <see cref="SqlExprBuilder"/> rejects an expression at BUILD time:
/// an unknown column, an unknown function, or a wrong-arity function call. The message always
/// names the offending symbol so a module author gets an actionable build-time error at the
/// point of construction — long before any dialect lowering or SQL execution. This is a
/// deliberately public, adapter-owned type (not a Core-internal exception) so an external module
/// with NO <c>InternalsVisibleTo</c> grant can catch it.
/// </summary>
public sealed class SqlExprBuildException : Exception
{
    public SqlExprBuildException(string message) : base(message) { }
}

/// <summary>
/// The closed allow-list of scalar functions a <see cref="SqlExpr.Fn"/> may name, together with
/// their argument arity. This is the public, build-time authority the <see cref="SqlExprBuilder"/>
/// validates against; it is kept in lock-step with the dialect-side function map
/// (<c>SqlDialectBase.MapFunctionName</c>) — a name absent here is rejected at build time, and a
/// name here that a dialect cannot spell is rejected at lowering, so the two layers can never
/// silently diverge into a pass-through.
/// </summary>
/// <remarks>
/// Arity is expressed as (min, max) where a null max means unbounded. Notable portable choices:
/// <list type="bullet">
///   <item><c>COALESCE</c> requires at least two arguments — a one-argument coalesce is pointless
///     and rejected by some engines.</item>
///   <item><c>ROUND</c> requires exactly two arguments (value, digits). SQL Server's <c>ROUND</c>
///     mandates the length argument, so the portable spelling always passes it; the one-argument
///     form that MySQL/PostgreSQL/SQLite tolerate is deliberately disallowed to keep "build once,
///     run on all four dialects" honest.</item>
/// </list>
/// </remarks>
public static class SqlExprFunctions
{
    private static readonly IReadOnlyDictionary<string, (int Min, int? Max)> _arities =
        new Dictionary<string, (int Min, int? Max)>(StringComparer.Ordinal)
        {
            ["UPPER"] = (1, 1),
            ["LOWER"] = (1, 1),
            ["LEN"] = (1, 1),
            ["ABS"] = (1, 1),
            ["ROUND"] = (2, 2),
            ["COALESCE"] = (2, null),
        };

    /// <summary>The canonical function names authors may pass to <see cref="SqlExprBuilder.Fn"/>.</summary>
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_arities.Keys;

    /// <summary>True when <paramref name="name"/> is an exact (case-sensitive) allow-list entry.</summary>
    public static bool IsKnown(string name) => _arities.ContainsKey(name);

    /// <summary>
    /// Validates a function call at build time. Throws <see cref="SqlExprBuildException"/> naming the
    /// offending symbol when the function is unknown or the argument count is out of range.
    /// </summary>
    public static void ValidateCall(string name, int argCount)
    {
        if (!_arities.TryGetValue(name, out var arity))
            throw new SqlExprBuildException(
                $"Unknown SQL function '{name}'. Allowed functions: {string.Join(", ", _arities.Keys)}.");

        var tooFew = argCount < arity.Min;
        var tooMany = arity.Max is int max && argCount > max;
        if (tooFew || tooMany)
        {
            var expected = arity.Max is null
                ? $"at least {arity.Min}"
                : arity.Min == arity.Max
                    ? $"exactly {arity.Min}"
                    : $"{arity.Min} to {arity.Max}";
            throw new SqlExprBuildException(
                $"SQL function '{name}' expects {expected} argument(s) but was called with {argCount}.");
        }
    }
}

/// <summary>
/// The public, fluent entry point a module author uses to build a validated <see cref="SqlExpr"/>
/// tree over the full slice-1 + slice-2 node set (<see cref="SqlExpr.Col"/>/<see cref="SqlExpr.Param"/>/
/// <see cref="SqlExpr.Lit"/>/<see cref="SqlExpr.Fn"/>/<see cref="SqlExpr.Case"/>/<see cref="SqlExpr.Cast"/>/
/// <see cref="SqlExpr.Concat"/>/<see cref="SqlExpr.DateAdd"/>/<see cref="SqlExpr.DateDiff"/>/
/// <see cref="SqlExpr.DatePart"/>/<see cref="SqlExpr.JsonGet"/>).
///
/// The builder is bound to one <see cref="IDbTable"/> and validates EAGERLY: a
/// <see cref="Col"/> reference is resolved against the table's real columns the moment it is
/// created, and every <see cref="Fn"/> call is checked against the closed function allow-list and
/// its arity. An author therefore gets a <see cref="SqlExprBuildException"/> at the point of
/// construction naming the offending symbol — never a deferred SQL-execution error.
///
/// Column resolution uses the SAME authority the query path uses
/// (<c>GraphQlLookup</c>-then-<c>ColumnLookup</c>, as in
/// <c>ComputedColumnDefinition.ResolveDependencyColumn</c>) and stores the resolved DB column name,
/// so a tree built once lowers correctly on every shipped dialect via
/// <see cref="ISqlDialect.LowerExpression"/>.
/// </summary>
public sealed class SqlExprBuilder
{
    private readonly IDbTable _table;

    private SqlExprBuilder(IDbTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
    }

    /// <summary>Creates a builder bound to <paramref name="table"/> — the schema every
    /// <see cref="Col"/> reference is validated against.</summary>
    public static SqlExprBuilder For(IDbTable table) => new(table);

    /// <summary>
    /// A column reference. Resolved eagerly against the table (GraphQL name or DB name); an unknown
    /// column throws <see cref="SqlExprBuildException"/> naming the column and table. The resolved DB
    /// column name is stored so the built tree lowers on every dialect without re-resolution.
    /// </summary>
    public Expr Col(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));

        if (_table.GraphQlLookup.TryGetValue(name, out var byGraphQl))
            return new Expr(this, new SqlExpr.Col(byGraphQl.DbName));
        if (_table.ColumnLookup.TryGetValue(name, out var byDb))
            return new Expr(this, new SqlExpr.Col(byDb.DbName));

        throw new SqlExprBuildException(
            $"Unknown column '{name}' referenced on table '{_table.GraphQlName}'. An expression column " +
            "must name a real GraphQL or database column of the table.");
    }

    /// <summary>A literal value. Lowers to a bound parameter, never interpolated text.</summary>
    public Expr Lit(object? value) => new(this, new SqlExpr.Lit(value));

    /// <summary>An explicitly bound parameter value with an optional provider DB type.</summary>
    public Expr Param(object? value, string? dbType = null) => new(this, new SqlExpr.Param(value, dbType));

    /// <summary>
    /// A function call over the closed allow-list (<c>UPPER/LOWER/LEN/ABS/ROUND/COALESCE</c>).
    /// The name and arity are validated eagerly; an unknown function or a wrong-arity call throws
    /// <see cref="SqlExprBuildException"/> naming the function.
    /// </summary>
    public Expr Fn(string name, params Expr[] args)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (args is null) throw new ArgumentNullException(nameof(args));

        SqlExprFunctions.ValidateCall(name, args.Length);
        return new Expr(this, new SqlExpr.Fn(name, args.Select(a => a.Node).ToList()));
    }

    /// <summary><c>UPPER(x)</c>.</summary>
    public Expr Upper(Expr value) => Fn("UPPER", value);

    /// <summary><c>LOWER(x)</c>.</summary>
    public Expr Lower(Expr value) => Fn("LOWER", value);

    /// <summary><c>LEN(x)</c> (lowered to <c>LENGTH</c> on the non-SQL-Server dialects).</summary>
    public Expr Len(Expr value) => Fn("LEN", value);

    /// <summary><c>ABS(x)</c>.</summary>
    public Expr Abs(Expr value) => Fn("ABS", value);

    /// <summary><c>ROUND(value, digits)</c> — the portable two-argument form.</summary>
    public Expr Round(Expr value, Expr digits) => Fn("ROUND", value, digits);

    /// <summary><c>COALESCE(a, b, ...)</c> — at least two arguments.</summary>
    public Expr Coalesce(params Expr[] values) => Fn("COALESCE", values);

    /// <summary>
    /// String concatenation, rendered with each dialect's own concat form (<c>+</c> / <c>||</c> /
    /// <c>CONCAT()</c>). Requires at least two parts.
    /// </summary>
    public Expr Concat(params Expr[] parts)
    {
        if (parts is null) throw new ArgumentNullException(nameof(parts));
        if (parts.Length < 2)
            throw new SqlExprBuildException(
                $"Concat requires at least two parts but was called with {parts.Length}.");
        return new Expr(this, new SqlExpr.Concat(parts.Select(p => p.Node).ToList()));
    }

    /// <summary>A CAST to a portable target type; the concrete storage type is chosen per dialect.</summary>
    public Expr Cast(Expr value, SqlExprType targetType) => new(this, new SqlExpr.Cast(value.Node, targetType));

    /// <summary>Starts a simple <c>CASE operand WHEN ... THEN ... [ELSE ...] END</c>. Call
    /// <see cref="CaseBuilder.When"/> at least once, then <see cref="CaseBuilder.End"/>.</summary>
    public CaseBuilder Case(Expr operand) => new(this, operand);

    /// <summary>Adds a signed amount of <paramref name="unit"/> to a temporal source.</summary>
    public Expr DateAdd(Expr source, DateUnit unit, Expr amount) =>
        new(this, new SqlExpr.DateAdd(source.Node, unit, amount.Node));

    /// <summary>The whole-<paramref name="unit"/> difference <c>end - start</c>. A dialect that cannot
    /// express a unit exactly (Postgres/SQLite month/year) throws
    /// <see cref="SqlExprLoweringNotSupportedException"/> at lowering — see the support matrix.</summary>
    public Expr DateDiff(DateUnit unit, Expr start, Expr end) =>
        new(this, new SqlExpr.DateDiff(unit, start.Node, end.Node));

    /// <summary>Extracts a single <paramref name="unit"/> field from a temporal source as an integer.</summary>
    public Expr DatePart(DateUnit unit, Expr source) => new(this, new SqlExpr.DatePart(unit, source.Node));

    /// <summary>Extracts a scalar from a JSON source at a validated identifier <paramref name="path"/>.
    /// Each segment must be a simple identifier (see <see cref="JsonPath"/>); unsafe text throws at
    /// construction.</summary>
    public Expr JsonGet(Expr source, params string[] path) =>
        new(this, new SqlExpr.JsonGet(source.Node, new JsonPath(path)));

    /// <summary>Internal factory used by <see cref="Expr"/> chaining so a wrapped node keeps its owner.</summary>
    internal Expr Wrap(SqlExpr node) => new(this, node);
}

/// <summary>
/// An immutable, validated expression node produced by <see cref="SqlExprBuilder"/>. It wraps the
/// underlying <see cref="SqlExpr"/> (available via <see cref="Node"/> or the implicit conversion)
/// and offers fluent chaining — <c>builder.Col("Name").Upper().Cast(SqlExprType.Text)</c> — where
/// each chained call re-enters the owning builder so function/arity validation still runs eagerly.
/// </summary>
public readonly struct Expr
{
    private readonly SqlExprBuilder _owner;

    internal Expr(SqlExprBuilder owner, SqlExpr node)
    {
        _owner = owner;
        Node = node;
    }

    /// <summary>The underlying immutable expression tree ready for
    /// <see cref="ISqlDialect.LowerExpression"/>.</summary>
    public SqlExpr Node { get; }

    /// <summary>Implicitly unwraps to the underlying <see cref="SqlExpr"/> so an <see cref="Expr"/>
    /// can be passed anywhere a node is expected (e.g. directly into a computed-column definition).</summary>
    public static implicit operator SqlExpr(Expr expr) => expr.Node;

    /// <summary><c>UPPER(this)</c>.</summary>
    public Expr Upper() => _owner.Upper(this);

    /// <summary><c>LOWER(this)</c>.</summary>
    public Expr Lower() => _owner.Lower(this);

    /// <summary><c>LEN(this)</c>.</summary>
    public Expr Len() => _owner.Len(this);

    /// <summary><c>ABS(this)</c>.</summary>
    public Expr Abs() => _owner.Abs(this);

    /// <summary><c>ROUND(this, digits)</c>.</summary>
    public Expr Round(Expr digits) => _owner.Round(this, digits);

    /// <summary><c>COALESCE(this, ...fallbacks)</c>.</summary>
    public Expr Coalesce(params Expr[] fallbacks) => _owner.Coalesce(Prepend(fallbacks));

    /// <summary><c>this || ...rest</c> using the dialect's concat form.</summary>
    public Expr Concat(params Expr[] rest) => _owner.Concat(Prepend(rest));

    /// <summary><c>CAST(this AS targetType)</c>.</summary>
    public Expr Cast(SqlExprType targetType) => _owner.Cast(this, targetType);

    /// <summary>Adds <paramref name="amount"/> of <paramref name="unit"/> to this temporal expression.</summary>
    public Expr DateAdd(DateUnit unit, Expr amount) => _owner.DateAdd(this, unit, amount);

    /// <summary>Extracts <paramref name="unit"/> from this temporal expression.</summary>
    public Expr DatePart(DateUnit unit) => _owner.DatePart(unit, this);

    /// <summary>Extracts a scalar from this JSON expression at <paramref name="path"/>.</summary>
    public Expr JsonGet(params string[] path) => _owner.JsonGet(this, path);

    private Expr[] Prepend(Expr[] rest)
    {
        var all = new Expr[rest.Length + 1];
        all[0] = this;
        Array.Copy(rest, 0, all, 1, rest.Length);
        return all;
    }
}

/// <summary>
/// Accumulates the branches of a simple <see cref="SqlExpr.Case"/> fluently. Created by
/// <see cref="SqlExprBuilder.Case"/>; call <see cref="When"/> one or more times, optionally
/// <see cref="Else"/>, then <see cref="End"/> to materialize the validated node. An empty CASE
/// (no WHEN branch) is rejected at <see cref="End"/>.
/// </summary>
public sealed class CaseBuilder
{
    private readonly SqlExprBuilder _owner;
    private readonly SqlExpr _operand;
    private readonly List<SqlExpr.CaseBranch> _branches = new();
    private SqlExpr? _else;

    internal CaseBuilder(SqlExprBuilder owner, Expr operand)
    {
        _owner = owner;
        _operand = operand.Node;
    }

    /// <summary>Adds one <c>WHEN when THEN then</c> branch.</summary>
    public CaseBuilder When(Expr when, Expr then)
    {
        _branches.Add(new SqlExpr.CaseBranch(when.Node, then.Node));
        return this;
    }

    /// <summary>Sets the optional <c>ELSE</c> result.</summary>
    public CaseBuilder Else(Expr elseValue)
    {
        _else = elseValue.Node;
        return this;
    }

    /// <summary>Materializes the validated <see cref="SqlExpr.Case"/>. Throws
    /// <see cref="SqlExprBuildException"/> if no WHEN branch was added.</summary>
    public Expr End()
    {
        if (_branches.Count == 0)
            throw new SqlExprBuildException(
                "A CASE expression must have at least one WHEN branch; an empty CASE is not valid SQL.");
        return _owner.Wrap(new SqlExpr.Case(_operand, _branches.ToArray(), _else));
    }
}
