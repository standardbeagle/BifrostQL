using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.PublicApi.Test;

/// <summary>
/// Stands in for an EXTERNAL third-party module: this assembly has NO
/// <c>InternalsVisibleTo</c> grant from BifrostQL.Core, so every type it touches below —
/// <see cref="SqlExprBuilder"/>, <see cref="Expr"/>, the <see cref="SqlExpr"/> nodes,
/// <see cref="ISqlDialect.LowerExpression"/>, and the <see cref="IDbTable"/>/<see cref="DbTable"/>/
/// <see cref="ColumnDto"/> it builds its schema from — is public. A green build here is the
/// compile-time proof of criterion 1; the assertions add criteria 2 (build once, lower on all four
/// dialects, parameterized) and 3 (eager build-time validation naming the offending symbol).
/// </summary>
public sealed class ExpressionBuilderConsumerTests
{
    // A schema built from ONLY public Core types — an external author has no test fixtures.
    private static IDbTable OrdersTable()
    {
        var columns = new[]
        {
            Column("Id", "int", isPk: true, graphQl: "id"),
            Column("Customer_Name", "nvarchar", graphQl: "customerName"),
            Column("Total", "decimal", graphQl: "total"),
            Column("Placed_At", "datetime", graphQl: "placedAt"),
            Column("Shipped_At", "datetime", graphQl: "shippedAt"),
            Column("Payload", "json", graphQl: "payload"),
        };

        return new DbTable
        {
            DbName = "Orders",
            GraphQlName = "Order",
            NormalizedName = "order",
            TableSchema = "dbo",
            ColumnLookup = columns.ToDictionary(c => c.DbName, c => c, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, c => c, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static ColumnDto Column(string name, string dataType, bool isPk = false, string? graphQl = null) =>
        new()
        {
            TableCatalog = "cat",
            TableSchema = "dbo",
            TableName = "Orders",
            ColumnName = name,
            GraphQlName = graphQl ?? name,
            NormalizedName = name.ToLowerInvariant(),
            DataType = dataType,
            IsPrimaryKey = isPk,
        };

    private static readonly ISqlDialect[] AllDialects =
    {
        SqlServerDialect.Instance,
        PostgresDialect.Instance,
        MySqlDialect.Instance,
        SqliteDialect.Instance,
    };

    /// <summary>
    /// The heart of the consumer seam: an external module builds ONE expression tree through the
    /// fluent builder — a labeled, upper-cased customer name concatenated with a rounded total —
    /// referencing columns by their GraphQL names, and lowers that single tree on every dialect.
    /// </summary>
    private static SqlExpr BuildOrderLabel(IDbTable table)
    {
        var b = SqlExprBuilder.For(table);
        return b.Case(b.Col("customerName").Upper())
            .When(b.Lit("VIP"), b.Concat(b.Lit("STAR:"), b.Col("customerName")))
            .Else(b.Col("total").Round(b.Lit(2)).Cast(SqlExprType.Text))
            .End();
    }

    // --- Criterion 2: define once, get parameterized SQL on all four dialects --------------------

    [Fact]
    public void ExpressionBuiltOnce_LowersToParameterizedSql_OnAllFourDialects()
    {
        var table = OrdersTable();
        var expr = BuildOrderLabel(table); // built exactly once, dialect-agnostic

        foreach (var dialect in AllDialects)
        {
            var parameters = new SqlParameterCollection();

            var sql = dialect.LowerExpression(expr, table, parameters);

            sql.Should().NotBeNullOrWhiteSpace();
            // Literals bind as parameters — none of the literal text reaches the SQL string.
            sql.Should().NotContain("VIP");
            sql.Should().NotContain("STAR:");
            sql.Should().Contain("@p", "literals lower to bound parameter placeholders");
            parameters.Parameters.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void FullNodeSet_BuildsAndLowers_OnAllFourDialects()
    {
        var table = OrdersTable();
        var b = SqlExprBuilder.For(table);

        // Slice-1 + slice-2 node coverage in a single build pass.
        var nodes = new[]
        {
            b.Col("customerName").Node,
            b.Lit("x").Node,
            b.Param("y", "nvarchar").Node,
            b.Upper(b.Col("customerName")).Node,
            b.Coalesce(b.Col("customerName"), b.Lit("n/a")).Node,
            b.Concat(b.Col("customerName"), b.Lit("!")).Node,
            b.Cast(b.Col("total"), SqlExprType.Text).Node,
            b.Case(b.Col("total")).When(b.Lit(0), b.Lit("zero")).Else(b.Lit("nonzero")).End().Node,
            b.DateAdd(b.Col("placedAt"), DateUnit.Day, b.Lit(3)).Node,
            b.DateDiff(DateUnit.Day, b.Col("placedAt"), b.Col("shippedAt")).Node,
            b.DatePart(DateUnit.Year, b.Col("placedAt")).Node,
            b.JsonGet(b.Col("payload"), "customer", "id").Node,
        };

        foreach (var dialect in AllDialects)
            foreach (var node in nodes)
            {
                var act = () => dialect.LowerExpression(node, table, new SqlParameterCollection());
                act.Should().NotThrow($"{node.GetType().Name} must lower on {dialect.GetType().Name}");
            }
    }

    // --- Criterion 3: an external author gets eager, build-time errors naming the symbol ---------

    [Fact]
    public void UnknownColumn_FailsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(OrdersTable());

        var act = () => b.Col("no_such_column");

        act.Should().Throw<SqlExprBuildException>().WithMessage("*no_such_column*");
    }

    [Fact]
    public void UnknownFunction_FailsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(OrdersTable());

        var act = () => b.Fn("BOGUS_FN", b.Col("customerName"));

        act.Should().Throw<SqlExprBuildException>().WithMessage("*BOGUS_FN*");
    }

    [Fact]
    public void WrongArity_FailsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(OrdersTable());

        var act = () => b.Fn("LOWER", b.Col("customerName"), b.Col("total"));

        act.Should().Throw<SqlExprBuildException>().WithMessage("*LOWER*");
    }
}
