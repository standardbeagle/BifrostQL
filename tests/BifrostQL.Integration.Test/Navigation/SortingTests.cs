using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Tests sorting (ORDER BY) against a real database.
/// </summary>
public abstract class SortingTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected SortingTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, List<string>? sort = null, int? limit = null)
    {
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        return new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = limit ?? -1,
            Sort = sort ?? new List<string>(),
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };
    }

    [Fact]
    public void SortAscending_ByName()
    {
        var query = BuildQuery("Categories", sort: new List<string> { "Name_asc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Categories"].data;
        var nameIndex = results["Categories"].index["Name"];
        var names = data.Select(row => row[nameIndex]?.ToString()).ToList();

        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public void SortDescending_ByName()
    {
        var query = BuildQuery("Categories", sort: new List<string> { "Name_desc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Categories"].data;
        var nameIndex = results["Categories"].index["Name"];
        var names = data.Select(row => row[nameIndex]?.ToString()).ToList();

        names.Should().BeInDescendingOrder();
    }

    [Fact]
    public void SortAscending_ByPrice()
    {
        var query = BuildQuery("Products", sort: new List<string> { "Price_asc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        var prices = data.Select(row => Convert.ToDecimal(row[priceIndex])).ToList();

        prices.Should().BeInAscendingOrder();
    }

    [Fact]
    public void SortDescending_ByPrice()
    {
        var query = BuildQuery("Products", sort: new List<string> { "Price_desc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        var prices = data.Select(row => Convert.ToDecimal(row[priceIndex])).ToList();

        prices.Should().BeInDescendingOrder();
    }

    [Fact]
    public void SortAscending_ById()
    {
        var query = BuildQuery("Products", sort: new List<string> { "Id_asc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var idIndex = results["Products"].index["Id"];
        var ids = data.Select(row => Convert.ToInt64(row[idIndex])).ToList();

        ids.Should().BeInAscendingOrder();
    }

    [Fact]
    public void SortWithPagination_MaintainsOrder()
    {
        // Sort all products by price ascending, then take page 2
        var allQuery = BuildQuery("Products", sort: new List<string> { "Price_asc" });
        var allResults = QueryExecutor.Execute(allQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var allData = allResults["Products"].data;
        var priceIndex = allResults["Products"].index["Price"];

        // Get page 2
        var pageQuery = BuildQuery("Products", sort: new List<string> { "Price_asc" }, limit: 5);
        pageQuery.Offset = 5;
        var pageResults = QueryExecutor.Execute(pageQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var pageData = pageResults["Products"].data;

        pageData.Count.Should().Be(5);
        for (var i = 0; i < 5; i++)
        {
            Convert.ToDecimal(pageData[i][priceIndex]).Should().Be(Convert.ToDecimal(allData[i + 5][priceIndex]));
        }
    }

    [Fact]
    public void MultiColumnSort()
    {
        // Sort by CategoryId asc, then Price desc
        var query = BuildQuery("Products", sort: new List<string> { "CategoryId_asc", "Price_desc" });
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var catIndex = results["Products"].index["CategoryId"];
        var priceIndex = results["Products"].index["Price"];

        // Verify CategoryId is non-decreasing
        var catIds = data.Select(row => Convert.ToInt64(row[catIndex])).ToList();
        catIds.Should().BeInAscendingOrder();

        // Within each category, prices should be descending
        var grouped = data.GroupBy(row => Convert.ToInt64(row[catIndex]));
        foreach (var group in grouped)
        {
            var prices = group.Select(row => Convert.ToDecimal(row[priceIndex])).ToList();
            prices.Should().BeInDescendingOrder();
        }
    }
}

// Concrete test classes per dialect
public sealed class SqliteSortingTests : SortingTestBase<SqliteTestDatabase>
{
    public SqliteSortingTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}
