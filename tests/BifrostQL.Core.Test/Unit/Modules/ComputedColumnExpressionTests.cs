using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Covers the structured <see cref="ComputedColumnKind.Expression"/> computed column: a single
/// author-supplied <see cref="SqlExpr"/> definition lowers to PARAMETERIZED SQL on every
/// dialect, dependency <see cref="SqlExpr.Col"/> references resolve through the same
/// <see cref="ComputedColumnDefinition.ResolveDependencyColumn"/> authority as the raw kind,
/// and a lexical blocklist is NOT the security boundary — attacker-shaped literal text emits a
/// bound parameter, never inline SQL.
/// </summary>
public sealed class ComputedColumnExpressionTests
{
    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar", graphQlName: "firstName")
                .WithColumn("last_name", "varchar", graphQlName: "lastName"))
            .Build();

    // UPPER({firstName}) || ' ' || {lastName} — deps named by GraphQL name to prove the
    // GraphQlLookup-then-ColumnLookup resolution, exercising Col, Fn, Lit, Concat in one tree.
    private static ComputedColumnDefinition FullNameExpr(string literal = " ")
        => new("fullName", "String", ComputedColumnKind.Expression, "expr", new[] { "firstName", "lastName" })
        {
            Expression = new SqlExpr.Concat(new SqlExpr[]
            {
                new SqlExpr.Fn("UPPER", new SqlExpr[] { new SqlExpr.Col("firstName") }),
                new SqlExpr.Lit(literal),
                new SqlExpr.Col("lastName"),
            }),
        };

    public static IEnumerable<object[]> Dialects()
    {
        yield return new object[] { SqlServerDialect.Instance, SqlFlavor.SqlServer };
        yield return new object[] { PostgresDialect.Instance, SqlFlavor.Postgres };
        yield return new object[] { MySqlDialect.Instance, SqlFlavor.MySql };
        yield return new object[] { SqliteDialect.Instance, SqlFlavor.Sqlite };
    }

    // --- Criterion 1: one definition lowers to valid, parameterized SQL on all four dialects -
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RenderExpression_LowersToParameterizedSql_OnEveryDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var table = BuildModel().GetTableFromDbName("People");
        var parameters = new SqlParameterCollection();

        var sql = FullNameExpr().RenderExpression(table, dialect, parameters);

        SqlSyntax.AssertValid($"SELECT {sql} AS Expr", flavor,
            $"lowered {dialect.GetType().Name} computed expression must be valid SQL");
        // Column names surface as validated identifiers; the space literal is a bound parameter.
        sql.Should().Contain("@p", "the literal separator lowers to a bound parameter");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(" ");
    }

    // --- Criterion 2: Col references resolve through ResolveDependencyColumn (graphql name) ---
    [Fact]
    public void RenderExpression_ResolvesColByGraphQlName_ToDbColumn()
    {
        var table = BuildModel().GetTableFromDbName("People");
        var parameters = new SqlParameterCollection();

        var sql = FullNameExpr().RenderExpression(table, SqlServerDialect.Instance, parameters);

        // firstName/lastName resolve to their DB names first_name/last_name and are escaped.
        sql.Should().Contain("[first_name]").And.Contain("[last_name]");
        sql.Should().NotContain("firstName").And.NotContain("lastName");
    }

    [Fact]
    public void RenderExpression_UnknownDependency_FailsWithSameErrorShapeAsRawKind()
    {
        var table = BuildModel().GetTableFromDbName("People");
        var def = new ComputedColumnDefinition(
            "bad", "String", ComputedColumnKind.Expression, "expr", new[] { "nope" })
        {
            Expression = new SqlExpr.Col("nope"),
        };

        var act = () => def.RenderExpression(table, SqlServerDialect.Instance, new SqlParameterCollection());

        // Identical message shape to ResolveDependencyColumn used by the raw kind.
        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*dependency 'nope' was not found*");
    }

    // --- Criterion 4: blocklist is NOT the boundary — attacker text in a Lit emits a param ----
    [Theory]
    [MemberData(nameof(Dialects))]
    public void RenderExpression_AttackerShapedLiteral_EmitsParameterNeverInlineSql(ISqlDialect dialect, SqlFlavor flavor)
    {
        const string attack = "'; DROP TABLE People; --";
        var table = BuildModel().GetTableFromDbName("People");
        var parameters = new SqlParameterCollection();

        // The very tokens the raw kind's lexical blocklist screens for (';', '--') pass straight
        // through here because they are DATA, not SQL: they land in a parameter value.
        var sql = FullNameExpr(literal: attack).RenderExpression(table, dialect, parameters);

        sql.Should().NotContain("DROP TABLE");
        sql.Should().NotContain("--");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(attack);
        SqlSyntax.AssertValid($"SELECT {sql} AS Expr", flavor,
            "attacker text in a literal must not corrupt the emitted SQL");
    }

    [Fact]
    public void RenderExpression_OnRawSqlKind_Throws()
    {
        var table = BuildModel().GetTableFromDbName("People");
        var def = new ComputedColumnDefinition(
            "x", "String", ComputedColumnKind.Sql, "{firstName}", new[] { "firstName" });

        var act = () => def.RenderExpression(table, SqlServerDialect.Instance, new SqlParameterCollection());

        act.Should().Throw<InvalidOperationException>();
    }
}
