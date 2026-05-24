using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.VisualQuery;
using BifrostQL.Core.Resolvers;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end coverage for the visual query builder: a VisualQuerySpec is turned
/// into SQL by <see cref="VisualQueryBuilder"/> and executed against a real
/// SQLite database via <see cref="RawSqlExecutor"/>, asserting the returned rows.
/// Covers single-table (criteria + sort + limit), inner join, left join, and a
/// composite-FK join.
/// </summary>
public sealed class VisualQueryEndToEndTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public VisualQueryEndToEndTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-vqe2e-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private async Task<IDbModel> SeedAndLoadAsync()
    {
        var stmts = new[]
        {
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER, total REAL, FOREIGN KEY (user_id) REFERENCES users(id))",
            "CREATE TABLE parent (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, name TEXT, PRIMARY KEY (tenant_id, id))",
            "CREATE TABLE child (id INTEGER PRIMARY KEY, tenant_id INTEGER, parent_id INTEGER, FOREIGN KEY (tenant_id, parent_id) REFERENCES parent(tenant_id, id))",
            "INSERT INTO users (id, name, age) VALUES (1,'alice',30),(2,'bob',20),(3,'carol',40)",
            "INSERT INTO orders (id, user_id, total) VALUES (10,1,100.0),(11,1,50.0),(12,2,75.0)",
            "INSERT INTO parent (tenant_id, id, name) VALUES (1,1,'p1'),(1,2,'p2')",
            "INSERT INTO child (id, tenant_id, parent_id) VALUES (100,1,1),(101,1,2)",
        };
        await using (var conn = _factory.GetConnection())
        {
            await conn.OpenAsync();
            foreach (var stmt in stmts)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        return await new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    private async Task<RawSqlResult> BuildAndRunAsync(IDbModel model, VisualQuerySpec spec)
    {
        var built = VisualQueryBuilder.Build(spec, model, SqliteDialect.Instance);
        return await RawSqlExecutor.ExecuteAsync(_factory, built.Sql, built.Parameters, 30, 1000);
    }

    [Fact]
    public async Task SingleTable_CriteriaSortLimit()
    {
        var model = await SeedAndLoadAsync();
        var spec = new VisualQuerySpec(
            [new VisualTable("users", null)],
            [
                new VisualColumn("users", "name", null, true, VisualSort.Asc, 1),
                new VisualColumn("users", "age", null, true, VisualSort.None, null),
            ],
            [],
            Filter: new VisualFilter(VisualFilterOp.Leaf, null,
                new VisualCriterion("users", "age", VisualFilterOperator.Gt, 25)),
            RowLimit: 10);

        var result = await BuildAndRunAsync(model, spec);

        // age > 25 → alice(30), carol(40); sorted by name asc → alice, carol.
        result.Rows.Should().HaveCount(2);
        result.Rows[0][0].Should().Be("alice");
        result.Rows[1][0].Should().Be("carol");
    }

    [Fact]
    public async Task InnerJoin_ReturnsMatchedRows()
    {
        var model = await SeedAndLoadAsync();
        var spec = new VisualQuerySpec(
            [new VisualTable("users", null), new VisualTable("orders", null)],
            [
                new VisualColumn("users", "name", null, true, VisualSort.None, null),
                new VisualColumn("orders", "total", null, true, VisualSort.None, null),
            ],
            [new VisualJoin("orders", ["user_id"], "users", ["id"], VisualJoinType.Inner)],
            Filter: null, RowLimit: 100);

        var result = await BuildAndRunAsync(model, spec);

        // alice has 2 orders, bob has 1, carol has none → 3 matched rows.
        result.Rows.Should().HaveCount(3);
        result.Rows.Select(r => r[0]).Should().OnlyContain(n => (string)n! == "alice" || (string)n! == "bob");
    }

    [Fact]
    public async Task LeftJoin_KeepsUnmatchedLeftRows()
    {
        var model = await SeedAndLoadAsync();
        var spec = new VisualQuerySpec(
            [new VisualTable("users", null), new VisualTable("orders", null)],
            [
                new VisualColumn("users", "name", null, true, VisualSort.Asc, 1),
                new VisualColumn("orders", "total", null, true, VisualSort.None, null),
            ],
            [new VisualJoin("orders", ["user_id"], "users", ["id"], VisualJoinType.Left)],
            Filter: null, RowLimit: 100);

        var result = await BuildAndRunAsync(model, spec);

        // carol has no orders but is kept with a NULL total under LEFT JOIN.
        result.Rows.Should().Contain(r => (string)r[0]! == "carol" && r[1] == null);
        result.Rows.Should().HaveCount(4); // alice x2, bob x1, carol x1
    }

    [Fact]
    public async Task CompositeJoin_MatchesOnAllKeyColumns()
    {
        var model = await SeedAndLoadAsync();
        var spec = new VisualQuerySpec(
            [new VisualTable("child", null), new VisualTable("parent", null)],
            [
                new VisualColumn("child", "id", null, true, VisualSort.Asc, 1),
                new VisualColumn("parent", "name", null, true, VisualSort.None, null),
            ],
            [new VisualJoin("child", ["tenant_id", "parent_id"], "parent", ["tenant_id", "id"], VisualJoinType.Inner)],
            Filter: null, RowLimit: 100);

        var result = await BuildAndRunAsync(model, spec);

        result.Rows.Should().HaveCount(2);
        result.Rows[0][1].Should().Be("p1"); // child 100 -> parent (1,1)=p1
        result.Rows[1][1].Should().Be("p2"); // child 101 -> parent (1,2)=p2
    }
}
