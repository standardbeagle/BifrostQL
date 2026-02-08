using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Tests combined filter + sort + paginate operations (full data grid scenario).
/// </summary>
public abstract class CombinedTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected CombinedTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, object? filter = null, List<string>? sort = null, int? limit = null, int? offset = null, bool includeResult = false)
    {
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        var query = new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = limit,
            Offset = offset,
            IncludeResult = includeResult,
            Sort = sort ?? new List<string>(),
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };

        if (filter != null)
        {
            query.Filter = TableFilter.FromObject(filter, tableName);
        }

        return query;
    }

    [Fact]
    public void FilterSortPaginate_DataGridScenario()
    {
        // Simulate: show Electronics products, sorted by price desc, page 1 of 2
        var filter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
        };
        var query = BuildQuery("Products",
            filter: filter,
            sort: new List<string> { "Price_desc" },
            limit: 2,
            offset: 0,
            includeResult: true);

        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        // Should get 2 rows
        results["Products"].data.Should().HaveCount(2);

        // Total count should be 4 (all electronics)
        Convert.ToInt32(results["Products=>count"].data[0][0]).Should().Be(TestSchema.Counts.ProductsPerCategory);

        // Prices should be descending
        var priceIndex = results["Products"].index["Price"];
        var prices = results["Products"].data.Select(row => Convert.ToDecimal(row[priceIndex])).ToList();
        prices.Should().BeInDescendingOrder();
    }

    [Fact]
    public void FilterSortPaginate_Page2()
    {
        // Get page 2 of the same query
        var filter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
        };

        // Page 1
        var page1Query = BuildQuery("Products",
            filter: filter,
            sort: new List<string> { "Price_desc" },
            limit: 2,
            offset: 0);
        var page1Results = QueryExecutor.Execute(page1Query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        // Page 2
        var page2Query = BuildQuery("Products",
            filter: filter,
            sort: new List<string> { "Price_desc" },
            limit: 2,
            offset: 2);
        var page2Results = QueryExecutor.Execute(page2Query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        page2Results["Products"].data.Should().HaveCount(2);

        // No overlap between pages
        var idIndex = page1Results["Products"].index["Id"];
        var page1Ids = page1Results["Products"].data.Select(row => row[idIndex]).ToHashSet();
        var page2Ids = page2Results["Products"].data.Select(row => row[idIndex]).ToHashSet();
        page1Ids.Overlaps(page2Ids).Should().BeFalse();

        // Page 2 prices should be <= page 1 prices (descending order)
        var priceIndex = page1Results["Products"].index["Price"];
        var lastPage1Price = Convert.ToDecimal(page1Results["Products"].data.Last()[priceIndex]);
        var firstPage2Price = Convert.ToDecimal(page2Results["Products"].data.First()[priceIndex]);
        firstPage2Price.Should().BeLessThanOrEqualTo(lastPage1Price);
    }

    [Fact]
    public void ComplexFilter_MultiSort_Paginate()
    {
        // Orders with Total > 200, sorted by Status asc then Total desc, page 1
        var filter = new Dictionary<string, object?>
        {
            { "Total", new Dictionary<string, object?> { { "_gt", 200m } } }
        };
        var query = BuildQuery("Orders",
            filter: filter,
            sort: new List<string> { "Status_asc", "Total_desc" },
            limit: 5,
            offset: 0,
            includeResult: true);

        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Orders"].data.Count.Should().BeLessThanOrEqualTo(5);

        var totalIndex = results["Orders"].index["Total"];
        results["Orders"].data.All(row => Convert.ToDecimal(row[totalIndex]) > 200m).Should().BeTrue();

        // Total count should reflect the filter
        var totalCount = Convert.ToInt32(results["Orders=>count"].data[0][0]);
        totalCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FilterWithJoin_CustomerOrdersFiltered()
    {
        // Get customers, join with orders filtered to Shipped status
        var db = Fixture.Database;
        var customersTable = db.DbModel.GetTableFromDbName("Customers");
        var ordersTable = db.DbModel.GetTableFromDbName("Orders");

        var customersQuery = new GqlObjectQuery
        {
            DbTable = customersTable,
            TableName = "Customers",
            GraphQlName = "Customers",
            SchemaName = customersTable.TableSchema,
            Limit = -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = customersTable.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };

        var ordersLink = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "orders",
            SchemaName = ordersTable.TableSchema,
            Limit = -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = ordersTable.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
            Filter = TableFilter.FromObject(
                new Dictionary<string, object?> { { "Status", new Dictionary<string, object?> { { "_eq", "Shipped" } } } },
                "Orders"),
        };

        customersQuery.Links.Add(ordersLink);
        customersQuery.ConnectLinks(db.DbModel);

        var results = QueryExecutor.Execute(customersQuery, db.DbModel, db.ConnFactory);

        results["Customers"].data.Should().HaveCount(TestSchema.Counts.Customers);

        var joinKey = customersQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);

        // All joined orders should be Shipped
        var statusIndex = results[joinKey].index["Status"];
        results[joinKey].data.All(row => row[statusIndex]?.ToString() == "Shipped").Should().BeTrue();
    }

    [Fact]
    public void EmptyFilterResult_WithPagination()
    {
        // Filter that returns no results
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_eq", "NonExistentProduct" } } }
        };
        var query = BuildQuery("Products",
            filter: filter,
            sort: new List<string> { "Id_asc" },
            limit: 10,
            offset: 0,
            includeResult: true);

        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Products"].data.Should().BeEmpty();
        Convert.ToInt32(results["Products=>count"].data[0][0]).Should().Be(0);
    }
}

// Concrete test classes per dialect
public sealed class SqliteCombinedTests : CombinedTestBase<SqliteTestDatabase>
{
    public SqliteCombinedTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}
