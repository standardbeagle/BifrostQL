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
    /// FIX 1 (SQL injection): NullLabel must be emitted as a parameter placeholder in the
    /// generated ISNULL expression, never interpolated literally into SQL.
    /// The label legitimately appears as an escaped pivot-column identifier (that is
    /// correct and unavoidable), but it must NEVER appear as a bare SQL string literal.
    /// </summary>
    [Fact]
    public void GenerateSqlServerPivot_MaliciousNullLabel_IsParameterizedNotLiteral()
    {
        const string maliciousLabel = "'; DROP TABLE t;--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: maliciousLabel);

        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues);

        // Security property 1: the label must NOT appear as a SQL string literal.
        // An unparameterized string literal would look like 'value'; only that form
        // can be exploited for injection. The label appearing as a delimited identifier
        // [value] is safe and expected.
        result.Sql.Should().NotContain($"'{maliciousLabel}'",
            because: "NullLabel must be bound as a parameter, not inlined as a SQL string literal");

        // Security property 2: the ISNULL expression must reference the parameter.
        result.Sql.Should().Contain("ISNULL(CAST([Status] AS NVARCHAR(MAX)), @p0)",
            because: "NullLabel should be passed as a bound parameter in ISNULL");

        // Security property 3: the parameter carries the actual value.
        result.Parameters.Should().ContainSingle(p => p.Name == "@p0",
            because: "one parameter should exist for the NullLabel binding");
        result.Parameters[0].Value.Should().Be(maliciousLabel,
            because: "the NullLabel value must be preserved in the parameter");
    }

    /// <summary>
    /// FIX 1 + EscapeIdentifier hardening: a NullLabel containing the SQL Server
    /// closing-bracket delimiter <c>]</c> must have that character doubled to <c>]]</c>
    /// inside the pivot column identifier. Without doubling the bracket would close the
    /// delimited identifier early and the rest of the label would execute as raw SQL.
    /// </summary>
    [Fact]
    public void GenerateSqlServerPivot_NullLabelWithClosingBracket_IdentifierDoublesDelimiter()
    {
        const string labelWithBracket = "x]; DROP TABLE t--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: labelWithBracket);

        var pivotValues = new List<object?> { null };

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues);

        // The escaped identifier must double the ] so the identifier never closes early.
        // Correct form:   [x]]; DROP TABLE t--]   (]] = escaped ] inside the brackets)
        // Breakout form:  [x]; DROP TABLE t--]     (] closes early → loose SQL)
        result.Sql.Should().Contain("[x]]; DROP TABLE t--]",
            because: "EscapeIdentifier must double ] to prevent SQL breakout");

        // The original unescaped form must NOT appear: x]; DROP TABLE t-- has a single ]
        // immediately followed by ;, which is the breakout sequence.
        result.Sql.Should().NotContain("x]; DROP TABLE t--",
            because: "a raw x]; sequence would indicate the ] was not doubled and breakout could occur");
    }

    /// <summary>
    /// FIX 1: Same injection guard for GenerateCaseWhenPivot — NullLabel appears only in the
    /// column alias (identifier), never as an unparameterized string literal in the CASE expression.
    /// </summary>
    [Fact]
    public void GenerateCaseWhenPivot_MaliciousNullLabel_DoesNotAppearAsStringLiteral()
    {
        const string maliciousLabel = "'; DROP TABLE t;--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: maliciousLabel);

        var pivotValues = new List<object?> { "Active", null };

        var result = PivotSqlGenerator.GenerateCaseWhenPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues);

        // The CASE WHEN branch for NULL does not interpolate the NullLabel as a literal value —
        // it only uses it as a column alias (escaped via EscapeIdentifier).
        // The body is "CASE WHEN [Status] IS NULL THEN [Id] END", no string literal involved.
        result.Sql.Should().NotContain($"'{maliciousLabel}'",
            because: "the null-branch CASE expression uses IS NULL, not a string comparison");
    }

    /// <summary>
    /// FIX 1: NullLabel parameter is appended after any existing filter parameters so
    /// parameter names do not collide.
    /// </summary>
    [Fact]
    public void GenerateSqlServerPivot_WithFilterAndMaliciousNullLabel_NullLabelParamIndexedAfterFilterParams()
    {
        const string maliciousLabel = "'; DROP TABLE t;--";

        var config = PivotQueryConfig.Create(
            "Status", "Id", "COUNT", new[] { "Region" },
            nullLabel: maliciousLabel);

        var pivotValues = new List<object?> { "Active" };
        var filter = new ParameterizedSql(
            " WHERE [Year] = @p0",
            new List<SqlParameterInfo> { new("@p0", 2024) });

        var result = PivotSqlGenerator.GenerateSqlServerPivot(
            Dialect, config, "[dbo].[Orders]", pivotValues, filter);

        result.Sql.Should().NotContain(maliciousLabel);

        // Filter param keeps its index; NullLabel gets the next index
        result.Parameters.Should().Contain(p => p.Name == "@p0" && (int)p.Value! == 2024);
        result.Parameters.Should().Contain(p => p.Name == "@p1" && (string)p.Value! == maliciousLabel);

        // ISNULL must reference the NullLabel parameter, not @p0 (which belongs to filter)
        result.Sql.Should().Contain("ISNULL(CAST([Status] AS NVARCHAR(MAX)), @p1)");
    }
}
