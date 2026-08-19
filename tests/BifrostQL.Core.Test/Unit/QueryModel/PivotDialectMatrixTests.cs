using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Dialect parity for pivot SQL generation: the pivot, empty-pivot, and
/// distinct-values statements must parse under every engine grammar (ScriptDom
/// for SQL Server, SqlParserCS for Postgres/MySQL/SQLite), and — the security
/// contract — a supplied transformer filter (tenant/soft-delete) must be spliced
/// into ALL THREE statements, so neither the pivot cells nor the column-header
/// distinct-value probe can read rows outside the caller's scope.
/// </summary>
public sealed class PivotDialectMatrixTests
{
    public static IEnumerable<object[]> Dialects => new[]
    {
        new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer },
        new object[] { PostgresDialect.Instance, SqlFlavor.Postgres },
        new object[] { MySqlDialect.Instance, SqlFlavor.MySql },
        new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite },
    };

    private static PivotQueryConfig Config() =>
        PivotQueryConfig.Create("status", "amount", "Sum", new[] { "region" });

    private static readonly object?[] PivotValues = { "open", "closed", null };

    private static ParameterizedSql TenantFilter(ISqlDialect dialect) =>
        new($" WHERE {dialect.EscapeIdentifier("tenant_id")} = @p0",
            new[] { new SqlParameterInfo("@p0", 1) });

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Pivot_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var tableRef = dialect.TableReference("dbo", "orders");
        var sql = PivotSqlGenerator.GeneratePivot(dialect, Config(), tableRef, PivotValues);

        // SQL Server routes to the native PIVOT operator (no GROUP BY); the other
        // dialects emit the CASE WHEN cross-tab. Both must parse for their grammar.
        SqlSyntax.AssertValid(sql.Sql, flavor, "pivot SQL");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void DistinctValues_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var tableRef = dialect.TableReference("dbo", "orders");
        var sql = PivotSqlGenerator.GenerateDistinctValuesSql(dialect, "status", tableRef);

        SqlSyntax.AssertValid(sql.Sql, flavor, "distinct pivot values SQL");
        sql.Sql.Should().Contain("DISTINCT");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void DistinctValues_WithLimit_IsBounded_AndParses(ISqlDialect dialect, SqlFlavor flavor)
    {
        // The distinct-value discovery must be row-bounded so a high-cardinality pivot
        // column cannot force the entire distinct set into memory before the caller's
        // cardinality guard runs (a DoS through an otherwise cheap authenticated request).
        var tableRef = dialect.TableReference("dbo", "orders");
        var sql = PivotSqlGenerator.GenerateDistinctValuesSql(dialect, "status", tableRef, filter: null, limit: 51);

        SqlSyntax.AssertValid(sql.Sql, flavor, "bounded distinct pivot values SQL");
        sql.Sql.Should().Contain("DISTINCT");
        // Every dialect must carry a row cap: SQL Server via FETCH NEXT, the rest via LIMIT.
        sql.Sql.Should().MatchRegex(@"FETCH NEXT 51 ROWS ONLY|LIMIT 51",
            "the discovery query must bound the distinct scan to MaxPivotColumns + 1 rows");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EmptyPivot_ParsesForDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var tableRef = dialect.TableReference("dbo", "orders");
        var sql = PivotSqlGenerator.GeneratePivot(dialect, Config(), tableRef, Array.Empty<object?>());

        SqlSyntax.AssertValid(sql.Sql, flavor, "empty pivot SQL");
        sql.Sql.Should().Contain("GROUP BY");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Filter_IsInjectedIntoAllPivotStatements(ISqlDialect dialect, SqlFlavor flavor)
    {
        var tableRef = dialect.TableReference("dbo", "orders");
        var filter = TenantFilter(dialect);

        var pivot = PivotSqlGenerator.GeneratePivot(dialect, Config(), tableRef, PivotValues, filter);
        var empty = PivotSqlGenerator.GeneratePivot(dialect, Config(), tableRef, Array.Empty<object?>(), filter);
        var distinct = PivotSqlGenerator.GenerateDistinctValuesSql(dialect, "status", tableRef, filter);

        foreach (var (name, built) in new[] { ("pivot", pivot), ("empty", empty), ("distinct", distinct) })
        {
            SqlSyntax.AssertValid(built.Sql, flavor, $"filtered {name} SQL");
            built.Sql.Should().Contain("WHERE", $"{name} must carry the scope filter");
            built.Parameters.Should().Contain(p => Equals(p.Value, 1), $"{name} must keep the filter parameter");
        }
    }
}
