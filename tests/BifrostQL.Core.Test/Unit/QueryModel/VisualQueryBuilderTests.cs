using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.QueryModel.VisualQuery;
using BifrostQL.Core.Resolvers;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for <see cref="VisualQueryBuilder"/> — spec → parameterized SQL across
/// all four dialects, with exact identifier quoting, parameterization, composite
/// joins, pagination, ScriptDom syntax validation, and injection-guard rejection.
/// </summary>
public sealed class VisualQueryBuilderTests
{
    // open/close quote chars per dialect — SQL Server brackets, the rest symmetric.
    public static IEnumerable<object[]> Dialects() => new[]
    {
        new object[] { SqlServerDialect.Instance, '[', ']' },
        new object[] { PostgresDialect.Instance, '"', '"' },
        new object[] { MySqlDialect.Instance, '`', '`' },
        new object[] { SqliteDialect.Instance, '"', '"' },
    };

    private static IDbModel TwoTableModel() => DbModelTestFixture.Create()
        .WithTable("users", t => t.WithSchema("dbo")
            .WithPrimaryKey("id", "int")
            .WithColumn("name", "nvarchar", isNullable: true)
            .WithColumn("tenant_id", "int"))
        .WithTable("orders", t => t.WithSchema("dbo")
            .WithPrimaryKey("id", "int")
            .WithColumn("user_id", "int")
            .WithColumn("tenant_id", "int")
            .WithColumn("total", "decimal", isNullable: true))
        .Build();

    private static VisualColumn Col(string table, string column, bool show = true,
        string? alias = null, string sort = VisualSort.None, int? sortOrder = null) =>
        new(table, column, alias, show, sort, sortOrder);

    private static VisualFilter Leaf(string table, string column, string op, object? value) =>
        new(VisualFilterOp.Leaf, null, new VisualCriterion(table, column, op, value));

    // ---- quoting & projection ----------------------------------------------

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SimpleSelect_QualifiesColumnsAndTable_PerDialect(ISqlDialect dialect, char open, char close)
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id"), Col("dbo.users", "name")],
            [], Filter: null, RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), dialect);

        result.Sql.Should().StartWith("SELECT ");
        result.Sql.Should().Contain($"{open}t0{close}.{open}id{close}");
        result.Sql.Should().Contain($"{open}t0{close}.{open}name{close}");
        // FROM emits the schema-qualified table aliased to t0.
        result.Sql.Should().Contain($"{open}dbo{close}.{open}users{close} AS {open}t0{close}");
        result.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Select_ColumnAlias_EmitsAsClause()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "name", alias: "full_name")],
            [], null, null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("[t0].[name] AS [full_name]");
    }

    // ---- joins -------------------------------------------------------------

    [Fact]
    public void InnerJoin_SingleColumn_EmitsOnCondition()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.orders", null)],
            [Col("dbo.users", "id"), Col("dbo.orders", "total")],
            [new VisualJoin("dbo.orders", ["user_id"], "dbo.users", ["id"], VisualJoinType.Inner)],
            Filter: null, RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("INNER JOIN [dbo].[orders] AS [t1]");
        result.Sql.Should().Contain("ON [t1].[user_id] = [t0].[id]");
    }

    [Fact]
    public void LeftJoin_EmitsLeftKeyword()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.orders", null)],
            [Col("dbo.users", "id")],
            [new VisualJoin("dbo.orders", ["user_id"], "dbo.users", ["id"], VisualJoinType.Left)],
            null, null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("LEFT JOIN [dbo].[orders] AS [t1]");
    }

    [Fact]
    public void CompositeJoin_AndsEveryColumnPair()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.orders", null)],
            [Col("dbo.users", "id")],
            [new VisualJoin("dbo.orders", ["user_id", "tenant_id"], "dbo.users", ["id", "tenant_id"], VisualJoinType.Inner)],
            null, null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("ON [t1].[user_id] = [t0].[id] AND [t1].[tenant_id] = [t0].[tenant_id]");
    }

    [Fact]
    public void DisconnectedTable_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.orders", null)],
            [Col("dbo.users", "id")],
            [], // no join connecting orders
            null, null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*not connected*");
    }

    // ---- WHERE / operators --------------------------------------------------

    [Fact]
    public void Where_Operators_AreParameterizedNeverInlined()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: new VisualFilter(VisualFilterOp.And,
            [
                Leaf("dbo.users", "name", VisualFilterOperator.Contains, "smith"),
                Leaf("dbo.users", "id", VisualFilterOperator.In, new[] { 1, 2, 3 }),
                Leaf("dbo.users", "tenant_id", VisualFilterOperator.Eq, 7),
                Leaf("dbo.users", "name", VisualFilterOperator.Null, false),
            ], Criterion: null),
            RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain(" WHERE (");
        result.Sql.Should().Contain("LIKE");
        result.Sql.Should().Contain("[t0].[id] IN (@p");
        result.Sql.Should().Contain("[t0].[tenant_id] = @p");
        result.Sql.Should().Contain("[t0].[name] IS NOT NULL");

        // contains(1) + in(3) + eq(1) = 5 params; null contributes none.
        result.Parameters.Should().HaveCount(5);
        result.Parameters.Values.Should().Contain(new object?[] { "smith", 1, 2, 3, 7 });
        // No literal injection of the values into the SQL text.
        result.Sql.Should().NotContain("smith");
        result.Sql.Should().NotContain(" 7");
    }

    [Fact]
    public void Where_Between_EmitsTwoParameters()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: Leaf("dbo.users", "id", VisualFilterOperator.Between, new[] { 10, 20 }),
            RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("[t0].[id] BETWEEN @p0 AND @p1");
        result.Parameters.Should().HaveCount(2);
        result.Parameters["@p0"].Should().Be(10);
        result.Parameters["@p1"].Should().Be(20);
    }

    [Fact]
    public void Where_Null_True_EmitsIsNull()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: Leaf("dbo.users", "name", VisualFilterOperator.Null, true),
            RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().Contain("[t0].[name] IS NULL");
        result.Parameters.Should().BeEmpty();
    }

    // ---- ORDER BY + pagination ---------------------------------------------

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RowLimit_UsesDialectPagination(ISqlDialect dialect, char open, char close)
    {
        _ = open; _ = close;
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id", sort: VisualSort.Asc, sortOrder: 1)],
            [], Filter: null, RowLimit: 50);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), dialect);

        if (dialect is SqlServerDialect)
        {
            result.Sql.Should().Contain("OFFSET");
            result.Sql.Should().Contain("FETCH NEXT 50");
        }
        else
        {
            result.Sql.Should().Contain("LIMIT 50");
        }
    }

    [Fact]
    public void RowLimit_ClampedToMaxRows()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [], Filter: null, RowLimit: 99999);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqliteDialect.Instance);

        result.Sql.Should().Contain($"LIMIT {VisualQueryBuilder.MaxRows}");
        result.Sql.Should().NotContain("99999");
    }

    // ---- ScriptDom syntactic validation ------------------------------------

    [Fact]
    public void GeneratedSqlServerSql_ParsesCleanly()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.orders", null)],
            [Col("dbo.users", "id", sort: VisualSort.Asc, sortOrder: 1), Col("dbo.orders", "total", alias: "amount")],
            [new VisualJoin("dbo.orders", ["user_id"], "dbo.users", ["id"], VisualJoinType.Left)],
            Filter: new VisualFilter(VisualFilterOp.And,
            [
                Leaf("dbo.users", "name", VisualFilterOperator.Contains, "a"),
                Leaf("dbo.orders", "total", VisualFilterOperator.Between, new[] { 1, 100 }),
            ], Criterion: null),
            RowLimit: 25);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(result.Sql);
        parser.Parse(reader, out var errors);

        errors.Should().BeEmpty(
            $"generated SQL must parse: {string.Join("; ", errors.Select(e => e.Message))}\nSQL: {result.Sql}");
    }

    // ---- injection guards / validation -------------------------------------

    [Fact]
    public void UnknownColumn_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "definitely_not_a_column")],
            [], null, null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*not found*");
    }

    [Fact]
    public void UnknownTable_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.ghost", null)],
            [Col("dbo.ghost", "id")],
            [], null, null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*not found*");
    }

    [Fact]
    public void NoColumnsShown_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id", show: false)],
            [], null, null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*at least one column*");
    }

    [Fact]
    public void DuplicateTableWithoutAlias_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null), new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [], null, null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*distinct alias*");
    }

    // ---- nested filter trees ------------------------------------------------

    [Fact]
    public void NestedAndOr_RendersGroupedParenthesesAndParameters()
    {
        // tenant_id = 7 AND (name CONTAINS smith OR id IN (1,2))
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: new VisualFilter(VisualFilterOp.And,
            [
                Leaf("dbo.users", "tenant_id", VisualFilterOperator.Eq, 7),
                new VisualFilter(VisualFilterOp.Or,
                [
                    Leaf("dbo.users", "name", VisualFilterOperator.Contains, "smith"),
                    Leaf("dbo.users", "id", VisualFilterOperator.In, new[] { 1, 2 }),
                ], Criterion: null),
            ], Criterion: null),
            RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        // Outer AND wraps an inner OR group: ( eq AND ( like OR in ) )
        result.Sql.Should().MatchRegex(@"WHERE \(.*tenant_id\] = @p\d+ AND \(.*LIKE.* OR .*IN \(@p\d+, @p\d+\)\)\)");
        result.Parameters.Should().HaveCount(4); // 7, smith, 1, 2
    }

    [Fact]
    public void EmptyAndGroup_ProducesNoWhereClause()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: new VisualFilter(VisualFilterOp.And, [], Criterion: null),
            RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        result.Sql.Should().NotContain("WHERE");
    }

    // ---- operator argument guards -------------------------------------------

    [Fact]
    public void EmptyIn_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: Leaf("dbo.users", "id", VisualFilterOperator.In, Array.Empty<int>()),
            RowLimit: null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*at least one value*");
    }

    [Fact]
    public void BetweenWrongArity_Throws()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: Leaf("dbo.users", "id", VisualFilterOperator.Between, new[] { 1, 2, 3 }),
            RowLimit: null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*exactly two values*");
    }

    [Fact]
    public void InWithBareString_Throws()
    {
        // A raw string must not be silently treated as a char array.
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", null)],
            [Col("dbo.users", "id")],
            [],
            Filter: Leaf("dbo.users", "name", VisualFilterOperator.In, "abc"),
            RowLimit: null);

        var act = () => VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*array of values*");
    }

    // ---- self-join via alias ------------------------------------------------

    [Fact]
    public void SelfJoin_WithAliases_QualifiesEachInstanceDistinctly()
    {
        // users joined to users (e.g. manager hierarchy) — disambiguated by alias.
        var spec = new VisualQuerySpec(
            [new VisualTable("dbo.users", "emp"), new VisualTable("dbo.users", "mgr")],
            [Col("emp", "id"), Col("mgr", "name")],
            [new VisualJoin("emp", ["tenant_id"], "mgr", ["id"], VisualJoinType.Inner)],
            Filter: null, RowLimit: null);

        var result = VisualQueryBuilder.Build(spec, TwoTableModel(), SqlServerDialect.Instance);

        // Both instances appear, each under its own alias.
        result.Sql.Should().Contain("[dbo].[users] AS [emp]");
        result.Sql.Should().Contain("[dbo].[users] AS [mgr]");
        result.Sql.Should().Contain("INNER JOIN");
    }
}
