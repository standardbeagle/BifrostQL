using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.ComputedColumns;

public enum ComputedColumnKind
{
    /// <summary>
    /// DEPRECATED raw-SQL computed column. The author string is emitted VERBATIM into SQL
    /// (only <c>{column}</c> placeholders are substituted) and is guarded solely by a lexical
    /// token blocklist — the one real injection surface on this feature, and non-portable
    /// across dialects. Prefer <see cref="Expression"/>, whose <see cref="SqlExpr"/> tree has
    /// NO verbatim-SQL path (every literal binds as a parameter, every column is validated).
    /// This kind is admin-gated: <see cref="ComputedColumnConfigCollector"/> rejects a table
    /// carrying it unless the model enables raw SQL (<c>raw-sql: enabled</c>).
    /// </summary>
    [Obsolete(ComputedColumnDefinition.RawSqlDeprecationMessage, error: false)]
    Sql,
    Provider,

    /// <summary>
    /// Structured expression computed column carrying an immutable <see cref="SqlExpr"/> tree,
    /// lowered per dialect via <see cref="ISqlDialect.LowerExpression"/>. The security
    /// boundary is parameterization + schema-validated identifiers, NOT a lexical blocklist:
    /// there is no path by which author-supplied text reaches the SQL string unparameterized.
    /// Replaces the deprecated raw-SQL <see cref="Sql"/> kind.
    /// </summary>
    Expression,
}

public sealed record ComputedColumnDefinition(
    string Name,
    string GraphQlType,
    ComputedColumnKind Kind,
    string ExpressionOrProvider,
    IReadOnlyList<string> Dependencies,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>
    /// Deprecation notice shared by the raw-SQL <see cref="ComputedColumnKind.Sql"/> enum member
    /// and <see cref="RenderSqlExpression"/>, so the runtime <c>[Obsolete]</c> warning matches the
    /// XML-doc deprecation wording. Points authors at the structured
    /// <see cref="ComputedColumnKind.Expression"/> kind, which has no verbatim-SQL path.
    /// </summary>
    internal const string RawSqlDeprecationMessage =
        "Raw-SQL computed columns (ComputedColumnKind.Sql / RenderSqlExpression) are deprecated and " +
        "admin-gated; migrate to ComputedColumnKind.Expression (computed-expr), which parameterizes " +
        "every value and has no verbatim-SQL path.";

    private static readonly Regex PlaceholderPattern = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex GraphQlNamePattern = new(@"^[_A-Za-z][_0-9A-Za-z]*$", RegexOptions.Compiled);

    public static bool IsValidGraphQlName(string value) => GraphQlNamePattern.IsMatch(value);

    /// <summary>
    /// The structured expression tree for a <see cref="ComputedColumnKind.Expression"/>
    /// column. Null for the other kinds. Lowered to parameterized, dialect-specific SQL by
    /// <see cref="RenderExpression"/>.
    /// </summary>
    public SqlExpr? Expression { get; init; }

    /// <summary>
    /// Lowers this <see cref="ComputedColumnKind.Expression"/> column to PARAMETERIZED,
    /// dialect-specific SQL. Every <see cref="SqlExpr.Col"/> in the tree is first resolved
    /// through <see cref="ResolveDependencyColumn"/> (the SAME GraphQlLookup-then-ColumnLookup
    /// authority the raw kind uses) so an unknown dependency fails fast with the identical
    /// error shape; every literal/parameter binds into <paramref name="parameters"/> rather
    /// than being interpolated. Unlike <see cref="RenderSqlExpression"/> there is no
    /// verbatim-SQL path and no lexical blocklist — the trust boundary is the lowering itself.
    /// </summary>
    public string RenderExpression(IDbTable table, ISqlDialect dialect, SqlParameterCollection parameters)
    {
        if (Kind != ComputedColumnKind.Expression)
            throw new InvalidOperationException("Only Expression computed columns can render expression SQL.");
        if (Expression is null)
            throw new BifrostExecutionError($"Computed expression column '{Name}' has no expression tree.");

        var resolved = ResolveColumns(Expression, table);
        return dialect.LowerExpression(resolved, table, parameters);
    }

    /// <summary>
    /// Rewrites every <see cref="SqlExpr.Col"/> name through <see cref="ResolveDependencyColumn"/>
    /// so the tree carries DB column names before lowering. This keeps a SINGLE
    /// column-resolution authority: a dependency may be named by GraphQL or DB name (as with
    /// the raw kind), and an unknown one throws the same <see cref="BifrostExecutionError"/>
    /// here, before the dialect's own <see cref="SqlExpr.Col"/> validation runs. Value nodes
    /// (<see cref="SqlExpr.Lit"/>/<see cref="SqlExpr.Param"/>) are returned unchanged — they
    /// carry no identifier to resolve and lower to bound parameters.
    /// </summary>
    private static SqlExpr ResolveColumns(SqlExpr expr, IDbTable table) => expr switch
    {
        SqlExpr.Col col => new SqlExpr.Col(ResolveDependencyColumn(table, col.Name)),
        SqlExpr.Fn fn => new SqlExpr.Fn(fn.Name, fn.Args.Select(a => ResolveColumns(a, table)).ToList()),
        SqlExpr.Cast cast => new SqlExpr.Cast(ResolveColumns(cast.Operand, table), cast.TargetType),
        SqlExpr.Concat concat => new SqlExpr.Concat(concat.Parts.Select(p => ResolveColumns(p, table)).ToList()),
        SqlExpr.Case @case => new SqlExpr.Case(
            ResolveColumns(@case.Operand, table),
            @case.Branches
                .Select(b => new SqlExpr.CaseBranch(ResolveColumns(b.When, table), ResolveColumns(b.Then, table)))
                .ToList(),
            @case.Else is null ? null : ResolveColumns(@case.Else, table)),
        _ => expr
    };

    /// <summary>
    /// DEPRECATED — renders a raw-SQL (<see cref="ComputedColumnKind.Sql"/>) computed column
    /// by verbatim emission of the author string with <c>{column}</c> substitution, guarded
    /// only by a lexical token blocklist. This is the injection surface this feature is
    /// migrating off; use <see cref="ComputedColumnKind.Expression"/> + <see cref="RenderExpression"/>,
    /// which parameterizes every value and has no verbatim path. Retained (admin-gated at
    /// config-collection) for backward compatibility only.
    /// </summary>
    [Obsolete(RawSqlDeprecationMessage, error: false)]
    public string RenderSqlExpression(IDbTable table, ISqlDialect dialect, string? tableAlias = null)
    {
        if (Kind != ComputedColumnKind.Sql)
            throw new InvalidOperationException("Only SQL computed columns can render SQL expressions.");

        if (ExpressionOrProvider.Contains(';')
            || ExpressionOrProvider.Contains("--", StringComparison.Ordinal)
            || ExpressionOrProvider.Contains("/*", StringComparison.Ordinal)
            || ExpressionOrProvider.Contains("*/", StringComparison.Ordinal))
            throw new BifrostExecutionError($"Computed SQL column '{Name}' contains unsupported SQL control tokens.");

        return PlaceholderPattern.Replace(ExpressionOrProvider, match =>
        {
            var requested = match.Groups["name"].Value;
            var dbColumn = ResolveDependencyColumn(table, requested);
            var escaped = dialect.EscapeIdentifier(dbColumn);
            return string.IsNullOrWhiteSpace(tableAlias)
                ? escaped
                : $"{dialect.EscapeIdentifier(tableAlias)}.{escaped}";
        });
    }

    public static string ResolveDependencyColumn(IDbTable table, string dependency)
    {
        if (table.GraphQlLookup.TryGetValue(dependency, out var byGraphQl))
            return byGraphQl.DbName;

        if (table.ColumnLookup.TryGetValue(dependency, out var byDb))
            return byDb.DbName;

        throw new BifrostExecutionError($"Computed column dependency '{dependency}' was not found on table '{table.GraphQlName}'.");
    }
}
