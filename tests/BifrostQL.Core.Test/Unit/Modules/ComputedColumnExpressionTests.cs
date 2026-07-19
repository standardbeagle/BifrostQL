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

// A few facts here deliberately construct the deprecated raw-SQL kind (ComputedColumnKind.Sql) to
// assert Expression-vs-Sql behavior and the admin gate, so the CS0618 obsolete-usage warnings are
// expected file-wide.
#pragma warning disable CS0618

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

    // --- Criterion 5: the collector parses computed-expr metadata into an Expression kind -----
    private static IDbModel BuildModelWithExprMetadata(string exprMetadata)
        => DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar", graphQlName: "firstName")
                .WithColumn("last_name", "varchar", graphQlName: "lastName")
                .WithMetadata(MetadataKeys.Computed.Expression, exprMetadata))
            .Build();

    [Fact]
    public void Collector_ParsesExpressionMetadata_IntoExpressionKind_ThatLowers()
    {
        var model = BuildModelWithExprMetadata("fullName:String:UPPER(firstName) || ' ' || lastName");
        var table = model.GetTableFromDbName("People");

        var computed = ComputedColumnConfigCollector.Find(table, "fullName")!;

        computed.Kind.Should().Be(ComputedColumnKind.Expression);
        computed.Dependencies.Should().BeEquivalentTo("firstName", "lastName");
        computed.Expression.Should().NotBeNull();

        var sql = computed.RenderExpression(table, PostgresDialect.Instance, new SqlParameterCollection());
        sql.Should().Contain("UPPER(\"first_name\")").And.Contain("\"last_name\"");
    }

    [Fact]
    public void Collector_ExpressionMetadata_ParsesNumericLiteralAsParameter()
    {
        var model = BuildModelWithExprMetadata("bumped:Float:ROUND(firstName)");
        var table = model.GetTableFromDbName("People");
        var computed = ComputedColumnConfigCollector.Find(table, "bumped")!;

        var parameters = new SqlParameterCollection();
        var sql = computed.RenderExpression(table, SqlServerDialect.Instance, parameters);

        sql.Should().Be("ROUND([first_name])");
    }

    [Fact]
    public void Collector_ExpressionMetadata_UnknownFunction_FailsFastNamingSymbol()
    {
        var model = BuildModelWithExprMetadata("bad:String:NOTAFUNCTION(firstName)");
        var table = model.GetTableFromDbName("People");
        var computed = ComputedColumnConfigCollector.Find(table, "bad")!;

        var act = () => computed.RenderExpression(table, SqlServerDialect.Instance, new SqlParameterCollection());

        act.Should().Throw<BifrostExecutionError>().WithMessage("*NOTAFUNCTION*");
    }

    [Fact]
    public void Collector_ExpressionMetadata_SyntaxError_FailsAtCollectionTime()
    {
        var model = BuildModelWithExprMetadata("bad:String:UPPER(firstName");
        var table = model.GetTableFromDbName("People");

        var act = () => ComputedColumnConfigCollector.FromTable(table);

        act.Should().Throw<BifrostExecutionError>();
    }

    // --- Criterion 3: raw-SQL kind is admin-gated, rejected at config-collection time ---------
    private static IDbModel BuildModelWithRawSql(bool rawSqlEnabled)
    {
        var fixture = DbModelTestFixture.Create();
        if (rawSqlEnabled)
            fixture = fixture.WithModelMetadata(MetadataKeys.RawSql.Enabled, "enabled");
        return fixture
            .WithTable("People", t => t
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar", graphQlName: "firstName")
                .WithColumn("last_name", "varchar", graphQlName: "lastName")
                .WithMetadata(MetadataKeys.Computed.Sql, "raw:String:{firstName}"))
            .Build();
    }

    [Fact]
    public void RawSqlComputedColumn_NonAdminModel_RejectedAtCollectionTime()
    {
        var model = BuildModelWithRawSql(rawSqlEnabled: false);
        var table = model.GetTableFromDbName("People");

        // The rejection happens at config-collection (the model-aware collector), NOT at query
        // time: no SQL is ever rendered for this column.
        var act = () => ComputedColumnConfigCollector.FromTable(table, model);

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*raw-SQL computed column*admin-gated*");
    }

    [Fact]
    public void RawSqlComputedColumn_AdminModel_IsCollected()
    {
        var model = BuildModelWithRawSql(rawSqlEnabled: true);
        var table = model.GetTableFromDbName("People");

        var definitions = ComputedColumnConfigCollector.FromTable(table, model);

        definitions.Should().ContainSingle(d => d.Kind == ComputedColumnKind.Sql)
            .Which.Name.Should().Be("raw");
    }

    // --- Criterion 5: computed-expr is in the metadata validation allow-list ------------------
    // Non-vacuous: the unknown-key gate still rejects a genuinely-unknown table key, and
    // accepts computed-expr — so the allow-list addition is doing real work.
    [Fact]
    public void ModelValidation_AcceptsComputedExprMetadataKey()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar", graphQlName: "firstName")
                .WithColumn("last_name", "varchar", graphQlName: "lastName")
                .WithMetadata(MetadataKeys.Computed.Expression,
                    "fullName:String:UPPER(firstName) || ' ' || lastName"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        // computed-expr must NOT be flagged as an unrecognized metadata key.
        act.Should().NotThrow();
    }

    [Fact]
    public void ModelValidation_RejectsGenuinelyUnknownTableKey()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("People", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("first_name", "varchar")
                .WithMetadata("computed-xpr", "fullName:String:first_name"))  // typo of computed-expr
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("computed-xpr").And.Contain("unrecognized");
    }
}
