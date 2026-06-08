using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.ComputedColumns;

public enum ComputedColumnKind
{
    Sql,
    Provider,
}

public sealed record ComputedColumnDefinition(
    string Name,
    string GraphQlType,
    ComputedColumnKind Kind,
    string ExpressionOrProvider,
    IReadOnlyList<string> Dependencies,
    IReadOnlyDictionary<string, string>? Options = null)
{
    private static readonly Regex PlaceholderPattern = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex GraphQlNamePattern = new(@"^[_A-Za-z][_0-9A-Za-z]*$", RegexOptions.Compiled);

    public static bool IsValidGraphQlName(string value) => GraphQlNamePattern.IsMatch(value);

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
