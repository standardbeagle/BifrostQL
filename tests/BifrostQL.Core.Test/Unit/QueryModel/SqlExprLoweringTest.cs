using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Verifies the <see cref="SqlExpr"/> tree lowers to valid, PARAMETERIZED SQL on every
/// shipped dialect via the Template-Method on <see cref="SqlDialectBase"/>, and that the
/// trust boundaries (column validation, closed function allow-list, no literal
/// interpolation) hold.
/// </summary>
public sealed class SqlExprLoweringTest
{
    private static IDbTable UsersTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("Name", "nvarchar"))
            .Build();
        return model.GetTableFromDbName("Users");
    }

    // Distinctive literal values chosen so they cannot appear as a substring of any escaped
    // identifier or SQL keyword in the emitted text — a robust "absent from SQL" assertion.
    private const string WhenLiteral = "ZZWHENVAL";
    private const string ThenLiteral = "ZZTHENVAL";
    private const string ElseLiteral = "ZZELSEVAL";

    // CASE UPPER(Name) WHEN 'ZZWHENVAL' THEN CAST(Id AS <text>) || 'ZZTHENVAL' ELSE 'ZZELSEVAL' END
    // Exercises Case over Fn(Col), with Cast and Concat in a single tree.
    private static SqlExpr NontrivialExpr() => new SqlExpr.Case(
        Operand: new SqlExpr.Fn("UPPER", new SqlExpr[] { new SqlExpr.Col("Name") }),
        Branches: new[]
        {
            new SqlExpr.CaseBranch(
                When: new SqlExpr.Lit(WhenLiteral),
                Then: new SqlExpr.Concat(new SqlExpr[]
                {
                    new SqlExpr.Cast(new SqlExpr.Col("Id"), SqlExprType.Text),
                    new SqlExpr.Lit(ThenLiteral)
                }))
        },
        Else: new SqlExpr.Lit(ElseLiteral));

    private static SqlFlavor FlavorOf(ISqlDialect dialect) => dialect switch
    {
        SqlServerDialect => SqlFlavor.SqlServer,
        PostgresDialect => SqlFlavor.Postgres,
        MySqlDialect => SqlFlavor.MySql,
        SqliteDialect => SqlFlavor.Sqlite,
        _ => throw new ArgumentException($"Unmapped dialect {dialect.GetType().Name}")
    };

    // --- Criterion 1: immutable records, no dialect dependency -------------------------

    [Fact]
    public void SqlExpr_NodesAreValueEqualImmutableRecords()
    {
        // Records give value equality; init-only positional members are immutable.
        new SqlExpr.Col("Name").Should().Be(new SqlExpr.Col("Name"));
        new SqlExpr.Lit(5).Should().Be(new SqlExpr.Lit(5));
        new SqlExpr.Col("Name").Should().NotBe(new SqlExpr.Col("Other"));
        new SqlExpr.Cast(new SqlExpr.Col("Id"), SqlExprType.Int)
            .Should().Be(new SqlExpr.Cast(new SqlExpr.Col("Id"), SqlExprType.Int));
    }

    // --- Criterion 2: nontrivial expression lowers to valid SQL on all four dialects ---

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void LowerExpression_NontrivialExpr_ValidOnEveryDialect(ISqlDialect dialect)
    {
        var parameters = new SqlParameterCollection();

        var sql = dialect.LowerExpression(NontrivialExpr(), UsersTable(), parameters);

        // Structural validity through a real grammar (ScriptDom for SQL Server, SqlParserCS
        // for the others) — wrapped in a SELECT so a bare expression parses as a statement.
        SqlSyntax.AssertValid($"SELECT {sql} AS Expr", FlavorOf(dialect),
            $"lowered {dialect.GetType().Name} expression must be valid SQL");
    }

    [Fact]
    public void LowerExpression_SqlServer_PassesScriptDomValidation()
    {
        var parameters = new SqlParameterCollection();

        var sql = SqlServerDialect.Instance.LowerExpression(NontrivialExpr(), UsersTable(), parameters);

        SqlSyntax.AssertValid($"SELECT {sql} AS Expr"); // defaults to ScriptDom / SQL Server
    }

    // --- Criterion 3: every literal/param is a bound parameter, absent from the SQL ----

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void LowerExpression_LiteralsBindAsParameters_NeverInterpolated(ISqlDialect dialect)
    {
        var parameters = new SqlParameterCollection();

        var sql = dialect.LowerExpression(NontrivialExpr(), UsersTable(), parameters);

        // Not one literal's text may appear in the SQL string.
        sql.Should().NotContain(WhenLiteral);
        sql.Should().NotContain(ThenLiteral);
        sql.Should().NotContain(ElseLiteral);
        sql.Should().Contain("@p", "literal values lower to bound parameter placeholders");

        // Each literal value is registered on the collection instead.
        var values = parameters.Parameters.Select(p => p.Value).ToList();
        values.Should().Contain(new object?[] { WhenLiteral, ThenLiteral, ElseLiteral });
        parameters.Parameters.Should().HaveCount(3);
    }

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void LowerExpression_ParamNode_BindsValueAsParameter(ISqlDialect dialect)
    {
        var parameters = new SqlParameterCollection();

        var sql = dialect.LowerExpression(new SqlExpr.Param("SECRETVAL"), UsersTable(), parameters);

        sql.Should().NotContain("SECRETVAL");
        sql.Should().StartWith("@p");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("SECRETVAL");
    }

    // --- Criterion 4: unknown function AND unknown column fail fast, naming the symbol -

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void LowerExpression_UnknownFunction_FailsFastNamingSymbol(ISqlDialect dialect)
    {
        var expr = new SqlExpr.Fn("NOTAFUNCTION", new SqlExpr[] { new SqlExpr.Col("Name") });

        var act = () => dialect.LowerExpression(expr, UsersTable(), new SqlParameterCollection());

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*NOTAFUNCTION*");
    }

    [Theory]
    [MemberData(nameof(CrossDialectTest.AllDialectData), MemberType = typeof(CrossDialectTest))]
    public void LowerExpression_UnknownColumn_FailsFastNamingSymbol(ISqlDialect dialect)
    {
        var expr = new SqlExpr.Col("NoSuchColumn");

        var act = () => dialect.LowerExpression(expr, UsersTable(), new SqlParameterCollection());

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*NoSuchColumn*");
    }

    // --- Criterion 5: concat uses the per-dialect operator, no branching in nodes ------

    public static IEnumerable<object[]> ConcatFormatData()
    {
        // dialect, expected concat form for two escaped columns
        yield return new object[] { SqlServerDialect.Instance, "[Name] + [Name]" };
        yield return new object[] { PostgresDialect.Instance, "\"Name\" || \"Name\"" };
        yield return new object[] { MySqlDialect.Instance, "CONCAT(`Name`, `Name`)" };
        yield return new object[] { SqliteDialect.Instance, "\"Name\" || \"Name\"" };
    }

    [Theory]
    [MemberData(nameof(ConcatFormatData))]
    public void LowerExpression_Concat_UsesDialectOperator(ISqlDialect dialect, string expected)
    {
        var expr = new SqlExpr.Concat(new SqlExpr[]
        {
            new SqlExpr.Col("Name"),
            new SqlExpr.Col("Name")
        });

        var sql = dialect.LowerExpression(expr, UsersTable(), new SqlParameterCollection());

        sql.Should().Be(expected);
    }

    // --- Function name mapping: LEN is SQL Server, LENGTH elsewhere --------------------

    [Fact]
    public void LowerExpression_LenFunction_MapsPerDialect()
    {
        var expr = new SqlExpr.Fn("LEN", new SqlExpr[] { new SqlExpr.Col("Name") });

        SqlServerDialect.Instance.LowerExpression(expr, UsersTable(), new SqlParameterCollection())
            .Should().Be("LEN([Name])");
        PostgresDialect.Instance.LowerExpression(expr, UsersTable(), new SqlParameterCollection())
            .Should().Be("LENGTH(\"Name\")");
        MySqlDialect.Instance.LowerExpression(expr, UsersTable(), new SqlParameterCollection())
            .Should().Be("LENGTH(`Name`)");
        SqliteDialect.Instance.LowerExpression(expr, UsersTable(), new SqlParameterCollection())
            .Should().Be("LENGTH(\"Name\")");
    }
}
