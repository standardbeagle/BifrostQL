using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Tests join navigation (OneToMany, ManyToOne) against a real database.
/// </summary>
public abstract class JoinTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected JoinTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, int? limit = null)
    {
        Fixture.EnsureAvailable();
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        return new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = limit ?? -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };
    }

    private GqlObjectQuery BuildLinkedQuery(string tableName)
    {
        Fixture.EnsureAvailable();
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        return new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };
    }

    [SkippableFact]
    public void ManyToOne_ProductToCategory()
    {
        // Products -> Categories (single link via CategoryId)
        var productsQuery = BuildQuery("Products");
        var categoriesLink = BuildLinkedQuery("Categories");
        categoriesLink.GraphQlName = "category";

        productsQuery.Links.Add(categoriesLink);
        productsQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(productsQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        // Should have the main Products result and the category join result
        results.Should().ContainKey("Products");
        results["Products"].data.Should().HaveCount(TestSchema.Counts.Products);

        // The join result should have category data with src_id referencing products
        var joinKey = productsQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinData = results[joinKey].data;
        joinData.Should().NotBeEmpty();

        // Each product should have a corresponding category row
        var srcIdIndex = results[joinKey].index["src_id"];
        var productIds = results["Products"].data
            .Select(row => row[results["Products"].index["Id"]])
            .ToHashSet();

        joinData.Select(row => row[srcIdIndex]).All(id => productIds.Contains(id)).Should().BeTrue();
    }

    [SkippableFact]
    public void OneToMany_CategoryToProducts()
    {
        // Categories -> Products (multi link via Id -> CategoryId)
        var categoriesQuery = BuildQuery("Categories");
        var productsLink = BuildLinkedQuery("Products");
        productsLink.GraphQlName = "products";

        categoriesQuery.Links.Add(productsLink);
        categoriesQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(categoriesQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("Categories");
        results["Categories"].data.Should().HaveCount(TestSchema.Counts.Categories);

        var joinKey = categoriesQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinData = results[joinKey].data;
        joinData.Should().HaveCount(TestSchema.Counts.Products); // All products linked back
    }

    /// <summary>
    /// A paged collection's per-parent total rides on the window's <c>__total</c> column.
    /// A window that keeps no row for a parent (a zero limit, or an offset past its rows)
    /// keeps that parent's row number 1 as the carrier of its total, on every dialect; the
    /// reader drops it from the page. The unpaged link is the control that counts the rows.
    /// </summary>
    [SkippableTheory]
    [InlineData(null, 0)]
    [InlineData(1000, 5)]
    public void OneToMany_PagedEmptyWindow_KeepsOneTotalCarrierPerParent(int? offset, int limit)
    {
        var allQuery = BuildQuery("Categories");
        var allLink = BuildLinkedQuery("Products");
        allLink.GraphQlName = "products";
        allQuery.Links.Add(allLink);
        allQuery.ConnectLinks(Fixture.Database.DbModel);
        var all = QueryExecutor.Execute(allQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory)[allQuery.Joins.First().JoinName];
        var expectedTotals = all.data
            .GroupBy(row => Convert.ToInt64(row[all.index["src_id"]]))
            .ToDictionary(g => g.Key, g => (long)g.Count());
        expectedTotals.Should().NotBeEmpty("control: the categories have products");

        var pagedQuery = BuildQuery("Categories");
        var pagedLink = BuildLinkedQuery("Products");
        pagedLink.GraphQlName = "products";
        pagedLink.IncludeResult = true;
        pagedLink.Offset = offset;
        pagedLink.Limit = limit;
        pagedQuery.Links.Add(pagedLink);
        pagedQuery.ConnectLinks(Fixture.Database.DbModel);
        var paged = QueryExecutor.Execute(pagedQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory)[pagedQuery.Joins.First().JoinName];

        var carriers = paged.data
            .GroupBy(row => Convert.ToInt64(row[paged.index["src_id"]]))
            .ToDictionary(g => g.Key, g => g.ToList());
        carriers.Keys.Should().BeEquivalentTo(expectedTotals.Keys);
        foreach (var (parent, rows) in carriers)
        {
            rows.Should().ContainSingle();
            Convert.ToInt64(rows[0][paged.index[PagedKeys.RowNumber]]).Should().Be(1);
            Convert.ToInt64(rows[0][paged.index[PagedKeys.Total]]).Should().Be(expectedTotals[parent]);
        }
    }

    [SkippableFact]
    public void OneToMany_OrderToItems()
    {
        // Orders -> OrderItems (multi link via Id -> OrderId)
        var ordersQuery = BuildQuery("Orders");
        var itemsLink = BuildLinkedQuery("OrderItems");
        itemsLink.GraphQlName = "items";

        ordersQuery.Links.Add(itemsLink);
        ordersQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(ordersQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("Orders");
        results["Orders"].data.Should().HaveCount(TestSchema.Counts.Orders);

        var joinKey = ordersQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        results[joinKey].data.Should().HaveCount(TestSchema.Counts.OrderItems);
    }

    [SkippableFact]
    public void NestedJoin_OrderToItemsToProduct()
    {
        // Orders -> OrderItems -> Products (two-level join)
        var ordersQuery = BuildQuery("Orders", limit: 5);
        var itemsLink = BuildLinkedQuery("OrderItems");
        itemsLink.GraphQlName = "items";

        var productsLink = BuildLinkedQuery("Products");
        productsLink.GraphQlName = "product";
        itemsLink.Links.Add(productsLink);

        ordersQuery.Links.Add(itemsLink);
        ordersQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(ordersQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("Orders");
        results["Orders"].data.Count.Should().BeLessThanOrEqualTo(5);

        // Should have both join levels
        var itemsJoinKey = ordersQuery.Joins.First().JoinName;
        results.Should().ContainKey(itemsJoinKey);
        results[itemsJoinKey].data.Should().NotBeEmpty();

        // The nested product join should also be present
        var productJoinKey = ordersQuery.Joins.First().ConnectedTable.Joins.First().JoinName;
        results.Should().ContainKey(productJoinKey);
        results[productJoinKey].data.Should().NotBeEmpty();
    }

    [SkippableFact]
    public void JoinWithChildPagination()
    {
        // Categories -> Products with limit on child
        var categoriesQuery = BuildQuery("Categories");
        var productsLink = BuildLinkedQuery("Products");
        productsLink.GraphQlName = "products";
        productsLink.Limit = 2; // Only first 2 products per category

        categoriesQuery.Links.Add(productsLink);
        categoriesQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(categoriesQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var joinKey = categoriesQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        // With limit 2 per category and 5 categories, we should get <= 10 items
        results[joinKey].data.Count.Should().BeLessThanOrEqualTo(10);
    }

    [SkippableFact]
    public void JoinWithFilter_OnParent()
    {
        // Filter categories to just Electronics, then get products
        var categoriesQuery = BuildQuery("Categories");
        categoriesQuery.Filter = TableFilter.FromObject(
            new Dictionary<string, object?> { { "Name", new Dictionary<string, object?> { { "_eq", "Electronics" } } } },
            categoriesQuery.DbTable!);

        var productsLink = BuildLinkedQuery("Products");
        productsLink.GraphQlName = "products";
        categoriesQuery.Links.Add(productsLink);
        categoriesQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(categoriesQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Categories"].data.Should().HaveCount(1);

        var joinKey = categoriesQuery.Joins.First().JoinName;
        results[joinKey].data.Should().HaveCount(TestSchema.Counts.ProductsPerCategory);
    }
}

// Concrete test classes per dialect
public sealed class SqliteJoinTests : JoinTestBase<SqliteTestDatabase>
{
    public SqliteJoinTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}
