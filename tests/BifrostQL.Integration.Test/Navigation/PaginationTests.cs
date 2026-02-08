using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Tests pagination (LIMIT/OFFSET) against a real database.
/// </summary>
public abstract class PaginationTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected PaginationTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, int? limit = null, int? offset = null, bool includeResult = false)
    {
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        return new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = limit,
            Offset = offset,
            IncludeResult = includeResult,
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };
    }

    [Fact]
    public void DefaultLimit_Returns100OrAllRows()
    {
        var query = BuildQuery("Products"); // 20 products, default limit is 100
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("Products");
        var data = results["Products"].data;
        data.Count.Should().Be(TestSchema.Counts.Products); // 20 < 100 default
    }

    [Fact]
    public void ExplicitLimit_ReturnsRequestedRows()
    {
        var query = BuildQuery("Products", limit: 5);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Products"].data.Count.Should().Be(5);
    }

    [Fact]
    public void OffsetAndLimit_ReturnsCorrectPage()
    {
        // Get all products first
        var allQuery = BuildQuery("Products", limit: -1);
        allQuery.Sort = new List<string> { "Id_asc" };
        var allResults = QueryExecutor.Execute(allQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var allData = allResults["Products"].data;

        // Now get page 2 (offset=5, limit=5)
        var pageQuery = BuildQuery("Products", limit: 5, offset: 5);
        pageQuery.Sort = new List<string> { "Id_asc" };
        var pageResults = QueryExecutor.Execute(pageQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var pageData = pageResults["Products"].data;

        pageData.Count.Should().Be(5);

        // Verify the offset data matches the corresponding slice of all data
        var idIndex = allResults["Products"].index["Id"];
        for (var i = 0; i < 5; i++)
        {
            pageData[i][idIndex].Should().Be(allData[i + 5][idIndex]);
        }
    }

    [Fact]
    public void PageThroughEntireDataset_NoDuplicatesOrGaps()
    {
        var pageSize = 7;
        var allIds = new List<object?>();
        var offset = 0;

        while (true)
        {
            var query = BuildQuery("Products", limit: pageSize, offset: offset);
            query.Sort = new List<string> { "Id_asc" };
            var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
            var data = results["Products"].data;

            if (data.Count == 0) break;

            var idIndex = results["Products"].index["Id"];
            allIds.AddRange(data.Select(row => row[idIndex]));
            offset += pageSize;

            if (data.Count < pageSize) break;
        }

        allIds.Should().HaveCount(TestSchema.Counts.Products);
        allIds.Distinct().Should().HaveCount(TestSchema.Counts.Products);
    }

    [Fact]
    public void IncludeResult_ReturnsTotalCount()
    {
        var query = BuildQuery("Products", limit: 5, includeResult: true);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("Products");
        results.Should().ContainKey("Products=>count");
        results["Products"].data.Count.Should().Be(5);

        var countData = results["Products=>count"].data;
        countData.Should().HaveCount(1);
        Convert.ToInt32(countData[0][0]).Should().Be(TestSchema.Counts.Products);
    }

    [Fact]
    public void OffsetBeyondData_ReturnsEmpty()
    {
        var query = BuildQuery("Products", limit: 10, offset: 1000);
        query.Sort = new List<string> { "Id_asc" };
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Products"].data.Count.Should().Be(0);
    }

    [Fact]
    public void UnlimitedQuery_ReturnsAllRows()
    {
        var query = BuildQuery("OrderItems", limit: -1);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["OrderItems"].data.Count.Should().Be(TestSchema.Counts.OrderItems);
    }
}

// Concrete test classes per dialect
public sealed class SqlitePaginationTests : PaginationTestBase<SqliteTestDatabase>
{
    public SqlitePaginationTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}
