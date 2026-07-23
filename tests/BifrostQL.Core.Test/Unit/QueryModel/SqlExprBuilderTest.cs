using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Verifies the public <see cref="SqlExprBuilder"/> validates EAGERLY (criterion 3): an unknown
/// column, an unknown function, and a wrong-arity call all throw <see cref="SqlExprBuildException"/>
/// at BUILD time — naming the offending symbol — before any dialect lowering runs. Also pins the
/// build-time function allow-list against the dialect-side lowering map (no silent drift) and the
/// per-dialect DateDiff truncation-vs-floor divergence carried as an advisory from SqlExpr 2.
/// </summary>
public sealed class SqlExprBuilderTest
{
    private static IDbTable UsersTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("FullName", "nvarchar", graphQlName: "fullName")
                .WithColumn("StartAt", "datetime")
                .WithColumn("EndAt", "datetime"))
            .Build();
        return model.GetTableFromDbName("Users");
    }

    // --- Criterion 3: unknown column fails at BUILD time, naming the symbol ----------------------

    [Fact]
    public void Col_UnknownColumn_ThrowsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(UsersTable());

        var act = () => b.Col("NoSuchColumn");

        act.Should().Throw<SqlExprBuildException>()
            .WithMessage("*NoSuchColumn*")
            .WithMessage("*Users*");
    }

    [Fact]
    public void Col_ResolvesByGraphQlNameAndByDbName()
    {
        var b = SqlExprBuilder.For(UsersTable());

        // Both the GraphQL alias and the DB name resolve to the same stored DB column.
        ((SqlExpr.Col)b.Col("fullName").Node).Name.Should().Be("FullName");
        ((SqlExpr.Col)b.Col("FullName").Node).Name.Should().Be("FullName");
    }

    // --- Criterion 3: unknown function fails at BUILD time, naming the symbol --------------------

    [Fact]
    public void Fn_UnknownFunction_ThrowsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(UsersTable());

        var act = () => b.Fn("NOTAFUNCTION", b.Col("FullName"));

        act.Should().Throw<SqlExprBuildException>()
            .WithMessage("*NOTAFUNCTION*");
    }

    // --- Criterion 3: wrong-arity call fails at BUILD time, naming the symbol --------------------

    [Fact]
    public void Fn_WrongArity_ThrowsAtBuildTime_NamingSymbol()
    {
        var b = SqlExprBuilder.For(UsersTable());

        // UPPER takes exactly one argument.
        var act = () => b.Fn("UPPER", b.Col("FullName"), b.Col("FullName"));

        act.Should().Throw<SqlExprBuildException>()
            .WithMessage("*UPPER*")
            .WithMessage("*argument*");
    }

    [Fact]
    public void Round_SingleArgument_RejectedAtBuildTime()
    {
        var b = SqlExprBuilder.For(UsersTable());

        // ROUND requires the portable (value, digits) form; the one-argument form is disallowed
        // because SQL Server's ROUND mandates the length argument.
        var act = () => b.Fn("ROUND", b.Col("Id"));

        act.Should().Throw<SqlExprBuildException>().WithMessage("*ROUND*");
    }

    [Fact]
    public void Coalesce_SingleArgument_RejectedAtBuildTime()
    {
        var b = SqlExprBuilder.For(UsersTable());

        var act = () => b.Coalesce(b.Col("FullName"));

        act.Should().Throw<SqlExprBuildException>().WithMessage("*COALESCE*");
    }

    [Fact]
    public void Concat_SinglePart_RejectedAtBuildTime()
    {
        var b = SqlExprBuilder.For(UsersTable());

        var act = () => b.Concat(b.Col("FullName"));

        act.Should().Throw<SqlExprBuildException>().WithMessage("*Concat*");
    }

    [Fact]
    public void Case_NoWhenBranch_RejectedAtBuildTime()
    {
        var b = SqlExprBuilder.For(UsersTable());

        var act = () => b.Case(b.Col("FullName")).End();

        act.Should().Throw<SqlExprBuildException>().WithMessage("*CASE*");
    }

    // --- Anti-drift: every build-time allow-list name is a real, lowerable dialect function ------
    // A name the builder permits but the dialect map rejects (or vice versa) would be a silent
    // divergence: build passes, lowering throws. This pins the two layers together.

    [Fact]
    public void FunctionAllowList_EveryName_LowersWithoutUnknownFunctionError()
    {
        var table = UsersTable();
        var b = SqlExprBuilder.For(table);

        foreach (var name in SqlExprFunctions.Names)
        {
            SqlExprFunctions.ValidateCall(name, name == "COALESCE" ? 2 : name == "ROUND" ? 2 : 1);

            // Build a minimally-valid call for the function and lower it on SQL Server — the
            // dialect that overrides MapFunctionName — asserting it is not rejected as unknown.
            Expr call = name switch
            {
                "ROUND" => b.Fn("ROUND", b.Col("Id"), b.Lit(2)),
                "COALESCE" => b.Fn("COALESCE", b.Col("FullName"), b.Lit("x")),
                _ => b.Fn(name, b.Col("FullName")),
            };

            var act = () => SqlServerDialect.Instance.LowerExpression(call.Node, table, new SqlParameterCollection());
            act.Should().NotThrow($"'{name}' is on the builder allow-list and must lower on SQL Server");
        }
    }

    // --- Advisory 2 (SqlExpr 2 review): FLOOR-vs-truncation divergence for NEGATIVE DateDiff -----
    // Postgres lowers DateDiff with FLOOR (rounds toward negative infinity); SQLite lowers with
    // CAST(... AS INTEGER) (truncates toward zero). For a negative interval the two diverge — this
    // pins the emitted form so the divergence documented in the guide stays true to the code.

    [Fact]
    public void DateDiff_NegativeIntervalDivergence_PinnedPerDialect()
    {
        var table = UsersTable();
        var b = SqlExprBuilder.For(table);
        // end < start yields a negative difference where FLOOR and truncation disagree.
        var diff = b.DateDiff(DateUnit.Day, b.Col("EndAt"), b.Col("StartAt"));

        var pg = PostgresDialect.Instance.LowerExpression(diff.Node, table, new SqlParameterCollection());
        var sqlite = SqliteDialect.Instance.LowerExpression(diff.Node, table, new SqlParameterCollection());

        pg.Should().Contain("FLOOR(", "PostgreSQL floors the epoch delta (toward negative infinity)");
        sqlite.Should().Contain("AS INTEGER", "SQLite truncates the Julian-day delta (toward zero)");
    }

    [Fact]
    public void DateDiff_WholeMonth_NotSupportedOnPostgresAndSqlite()
    {
        var table = UsersTable();
        var b = SqlExprBuilder.For(table);
        var monthDiff = b.DateDiff(DateUnit.Month, b.Col("StartAt"), b.Col("EndAt"));

        var pg = () => PostgresDialect.Instance.LowerExpression(monthDiff.Node, table, new SqlParameterCollection());
        var sqlite = () => SqliteDialect.Instance.LowerExpression(monthDiff.Node, table, new SqlParameterCollection());

        pg.Should().Throw<SqlExprLoweringNotSupportedException>();
        sqlite.Should().Throw<SqlExprLoweringNotSupportedException>();

        // SQL Server and MySQL count calendar boundaries natively — no throw.
        b.DateDiff(DateUnit.Month, b.Col("StartAt"), b.Col("EndAt")); // build is dialect-agnostic
        var mssql = () => SqlServerDialect.Instance.LowerExpression(monthDiff.Node, table, new SqlParameterCollection());
        var mysql = () => MySqlDialect.Instance.LowerExpression(monthDiff.Node, table, new SqlParameterCollection());
        mssql.Should().NotThrow();
        mysql.Should().NotThrow();
    }
}
