using FluentAssertions;
using BifrostQL.Core.QueryModel;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Security and correctness tests for PivotSqlGenerator.
/// Covers SQL-injection hardening added in fix/dotnet-review-2026-06-10.
/// </summary>
public sealed class PivotSqlGeneratorTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    /// <summary>
    /// SQL Server pivot labels are data and must reach the server only as bound
    /// parameters, as on every other dialect. The native T-SQL PIVOT wrote each string
    /// label (and the null label) into its <c>IN (...)</c> list as an escaped identifier,
    /// so client data sat in SQL text behind a bracket-escaping and collation-folding
    /// guard. The SQL is tokenized with ScriptDom: no identifier or string-literal token
    /// may carry a label, and each non-null label is bound to its own variable token.
    /// </summary>
    [Fact]
    public void SqlServerPivot_BindsEveryLabelAsAParameter_AndWritesNoneIntoSqlText()
    {
        const string injectionLabel = "x]; DROP TABLE t--";
        var config = PivotQueryConfig.Create("Status", "Id", "COUNT", new[] { "Region" }, nullLabel: "Unknown");
        var pivotValues = new List<object?> { "Active", injectionLabel, null };

        var result = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, "[dbo].[Orders]", pivotValues);

        var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(new StringReader(result.Sql), out var errors);
        errors.Should().BeEmpty();
        var labels = new[] { "Active", injectionLabel, "Unknown" };
        var dataTokens = tokens
            .Where(t => t.TokenType is Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.QuotedIdentifier
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Identifier
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.AsciiStringLiteral
                or Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.UnicodeStringLiteral)
            .Select(t => Unquote(t.Text))
            .ToList();
        dataTokens.Should().Contain("Status", "the control: the pivot column is an identifier token");
        dataTokens.Should().NotIntersectWith(labels, "a pivot label must never be SQL text");

        var variables = tokens
            .Where(t => t.TokenType == Microsoft.SqlServer.TransactSql.ScriptDom.TSqlTokenType.Variable)
            .Select(t => t.Text)
            .ToList();
        variables.Should().Equal("@p0", "@p1");
        result.Parameters.Select(p => (p.Name, p.Value)).Should().Equal(("@p0", (object?)"Active"), ("@p1", injectionLabel));
    }

    private static string Unquote(string text) => text switch
    {
        ['[', .. var inner, ']'] => inner.Replace("]]", "]"),
        ['N', '\'', .. var inner, '\''] => inner.Replace("''", "'"),
        ['\'', .. var inner, '\''] => inner.Replace("''", "'"),
        _ => text,
    };

    /// <summary>
    /// The NULL arm compares with IS NULL, so the null label never appears as a string literal.
    /// </summary>
    [Fact]
    public void GeneratePivot_MaliciousNullLabel_DoesNotAppearAsStringLiteral()
    {
        const string maliciousLabel = "'; DROP TABLE t;--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: maliciousLabel);

        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[dbo].[Orders]", pivotValues);

        // The body is "CASE WHEN [Status] IS NULL THEN [Id] END", no string literal involved.
        result.Sql.Should().NotContain($"'{maliciousLabel}'",
            because: "the null-branch CASE expression uses IS NULL, not a string comparison");
    }

    /// <summary>
    /// Pivot-value parameters are numbered after the filter's, so a value never takes the
    /// filter's <c>@p0</c>. The null label is never SQL text or a parameter: the NULL arm is
    /// <c>IS NULL</c>.
    /// </summary>
    [Fact]
    public void GeneratePivot_WithFilter_NumbersValueParamsAfterFilterParams()
    {
        const string maliciousLabel = "'; DROP TABLE t;--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: maliciousLabel);

        var pivotValues = new List<object?> { "Active", null };
        var filter = new ParameterizedSql(
            " WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GeneratePivot(
            Dialect, config, "[dbo].[Orders]", pivotValues, filter);

        result.Sql.Should().NotContain(maliciousLabel);
        result.Parameters.Select(p => (p.Name, p.Value)).Should().Equal(("@p0", (object?)2024), ("@p1", "Active"));
        result.Sql.Should().Contain("CASE WHEN [Status] = @p1 THEN [Id] END");
        result.Sql.Should().Contain("CASE WHEN [Status] IS NULL THEN [Id] END");
    }
}
