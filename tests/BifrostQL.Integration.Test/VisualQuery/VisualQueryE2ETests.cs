using BifrostQL.Core.QueryModel.VisualQuery;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.VisualQuery;

/// <summary>
/// End-to-end coverage for the visual query builder against a real database. The
/// builder emits parameterized SQL per dialect; these tests execute that SQL on
/// the live engine (SQLite always; SQL Server / Postgres / MySQL when their
/// BIFROST_TEST_* env var is set) and assert the rows actually come back. This is
/// the only place the generated SQL is checked against each engine's real parser
/// and execution semantics rather than ScriptDom / string assertions.
/// </summary>
public abstract class VisualQueryE2EBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected VisualQueryE2EBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private List<Dictionary<string, object?>> Run(VisualQuerySpec spec)
    {
        Fixture.EnsureAvailable();
        var db = Fixture.Database;
        var built = VisualQueryBuilder.Build(spec, db.DbModel, db.Dialect);
        var ps = built.Parameters.Select(kv => (kv.Key, kv.Value)).ToArray();
        return QueryExecutor.ExecuteRaw(db.ConnFactory, built.Sql, ps);
    }

    private static VisualColumn Col(string table, string column, bool show = true,
        string? alias = null, string sort = VisualSort.None, int? sortOrder = null) =>
        new(table, column, alias, show, sort, sortOrder);

    private static VisualFilter Leaf(string table, string column, string op, object? value) =>
        new(VisualFilterOp.Leaf, null, new VisualCriterion(table, column, op, value));

    [SkippableFact]
    public void Equality_ReturnsMatchingRow()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("Products", null)],
            [Col("Products", "Name"), Col("Products", "Price")],
            [],
            Filter: Leaf("Products", "Name", VisualFilterOperator.Eq, "Laptop"),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(1);
        rows[0]["Name"]!.ToString().Should().Be("Laptop");
    }

    [SkippableFact]
    public void GreaterThan_WithDescendingSort_OrdersAndFilters()
    {
        // 5 products cost more than 100: Laptop, Phone, Tablet, Headphones, Lawn Mower.
        var spec = new VisualQuerySpec(
            [new VisualTable("Products", null)],
            [Col("Products", "Name"), Col("Products", "Price", sort: VisualSort.Desc, sortOrder: 1)],
            [],
            Filter: Leaf("Products", "Price", VisualFilterOperator.Gt, 100),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(5);
        rows[0]["Name"]!.ToString().Should().Be("Laptop"); // most expensive first
    }

    [SkippableFact]
    public void InnerJoin_FiltersOnParentTable()
    {
        // Products in the "Electronics" category — 4 of them.
        var spec = new VisualQuerySpec(
            [new VisualTable("Products", null), new VisualTable("Categories", null)],
            [Col("Products", "Name")],
            [new VisualJoin("Products", ["CategoryId"], "Categories", ["Id"], VisualJoinType.Inner)],
            Filter: Leaf("Categories", "Name", VisualFilterOperator.Eq, "Electronics"),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(4);
        rows.Select(r => r["Name"]!.ToString())
            .Should().BeEquivalentTo("Laptop", "Phone", "Tablet", "Headphones");
    }

    [SkippableFact]
    public void In_MatchesAnyOfList()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("Customers", null)],
            [Col("Customers", "Name"), Col("Customers", "City")],
            [],
            Filter: Leaf("Customers", "City", VisualFilterOperator.In, new[] { "New York", "Chicago" }),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(2);
        rows.Select(r => r["City"]!.ToString()).Should().OnlyContain(c => c == "New York" || c == "Chicago");
    }

    [SkippableFact]
    public void Contains_EmitsWorkingLikePattern()
    {
        // Every seeded customer email is at example.com.
        var spec = new VisualQuerySpec(
            [new VisualTable("Customers", null)],
            [Col("Customers", "Email")],
            [],
            Filter: Leaf("Customers", "Email", VisualFilterOperator.Contains, "@example.com"),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(TestSchema.Counts.Customers);
        rows.Should().OnlyContain(r => r["Email"]!.ToString()!.Contains("@example.com"));
    }

    [SkippableFact]
    public void Between_BoundsAreInclusive()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("Products", null)],
            [Col("Products", "Name"), Col("Products", "Price")],
            [],
            Filter: Leaf("Products", "Price", VisualFilterOperator.Between, new[] { 40, 50 }),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => Convert.ToDecimal(r["Price"]) >= 40m && Convert.ToDecimal(r["Price"]) <= 50m);
    }

    [SkippableFact]
    public void Null_FiltersIsNotNull()
    {
        // Every customer has a City in the seed, so IS NOT NULL returns all.
        var spec = new VisualQuerySpec(
            [new VisualTable("Customers", null)],
            [Col("Customers", "Name")],
            [],
            Filter: Leaf("Customers", "City", VisualFilterOperator.Null, false),
            RowLimit: null);

        var rows = Run(spec);

        rows.Should().HaveCount(TestSchema.Counts.Customers);
    }

    [SkippableFact]
    public void RowLimit_CapsResults()
    {
        var spec = new VisualQuerySpec(
            [new VisualTable("Orders", null)],
            [Col("Orders", "Id", sort: VisualSort.Asc, sortOrder: 1)],
            [],
            Filter: null,
            RowLimit: 5);

        var rows = Run(spec);

        rows.Should().HaveCount(5);
    }

    [SkippableFact]
    public void NestedAndOr_ExecutesGroupedLogic()
    {
        // Electronics products that are either cheap (< 200) OR named 'Laptop':
        // Phone(699) no, Tablet(449) no, Headphones(149) yes, Laptop(999) yes-by-name.
        var spec = new VisualQuerySpec(
            [new VisualTable("Products", null), new VisualTable("Categories", null)],
            [Col("Products", "Name")],
            [new VisualJoin("Products", ["CategoryId"], "Categories", ["Id"], VisualJoinType.Inner)],
            Filter: new VisualFilter(VisualFilterOp.And,
            [
                Leaf("Categories", "Name", VisualFilterOperator.Eq, "Electronics"),
                new VisualFilter(VisualFilterOp.Or,
                [
                    Leaf("Products", "Price", VisualFilterOperator.Lt, 200),
                    Leaf("Products", "Name", VisualFilterOperator.Eq, "Laptop"),
                ], Criterion: null),
            ], Criterion: null),
            RowLimit: null);

        var rows = Run(spec);

        rows.Select(r => r["Name"]!.ToString()).Should().BeEquivalentTo("Laptop", "Headphones");
    }
}

// Concrete per-dialect classes. SQLite always runs; the others skip unless their
// BIFROST_TEST_* connection-string env var is set.

public sealed class SqliteVisualQueryE2ETests : VisualQueryE2EBase<SqliteTestDatabase>
{
    public SqliteVisualQueryE2ETests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresVisualQueryE2ETests : VisualQueryE2EBase<PostgresTestDatabase>
{
    public PostgresVisualQueryE2ETests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlVisualQueryE2ETests : VisualQueryE2EBase<MySqlTestDatabase>
{
    public MySqlVisualQueryE2ETests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}

public sealed class SqlServerVisualQueryE2ETests : VisualQueryE2EBase<SqlServerTestDatabase>
{
    public SqlServerVisualQueryE2ETests(DatabaseFixture<SqlServerTestDatabase> fixture) : base(fixture) { }
}
