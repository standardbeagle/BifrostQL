using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Tests filtering (WHERE clause) against a real database.
/// </summary>
public abstract class FilteringTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected FilteringTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, object? filter = null)
    {
        Fixture.EnsureAvailable();
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        var query = new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };

        if (filter != null)
        {
            query.Filter = TableFilter.FromObject(filter, tableName);
        }

        return query;
    }

    // --- Equality ---

    [SkippableFact]
    public void EqualityFilter_String()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_eq", "Laptop" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().HaveCount(1);
        data[0][nameIndex]?.ToString().Should().Be("Laptop");
    }

    [SkippableFact]
    public void EqualityFilter_Int()
    {
        var filter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        data.Should().HaveCount(TestSchema.Counts.ProductsPerCategory);
    }

    [SkippableFact]
    public void NotEqualFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Status", new Dictionary<string, object?> { { "_neq", "Pending" } } }
        };
        var query = BuildQuery("Orders", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Orders"].data;
        var statusIndex = results["Orders"].index["Status"];
        data.Should().NotBeEmpty();
        data.All(row => row[statusIndex]?.ToString() != "Pending").Should().BeTrue();
    }

    // --- Comparison ---

    [SkippableFact]
    public void GreaterThanFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_gt", 100m } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        data.Should().NotBeEmpty();
        data.All(row => Convert.ToDecimal(row[priceIndex]) > 100m).Should().BeTrue();
    }

    [SkippableFact]
    public void LessThanFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_lt", 30m } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        data.Should().NotBeEmpty();
        data.All(row => Convert.ToDecimal(row[priceIndex]) < 30m).Should().BeTrue();
    }

    [SkippableFact]
    public void GreaterThanOrEqualFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Stock", new Dictionary<string, object?> { { "_gte", 200 } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var stockIndex = results["Products"].index["Stock"];
        data.Should().NotBeEmpty();
        data.All(row => Convert.ToInt32(row[stockIndex]) >= 200).Should().BeTrue();
    }

    [SkippableFact]
    public void LessThanOrEqualFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Stock", new Dictionary<string, object?> { { "_lte", 30 } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var stockIndex = results["Products"].index["Stock"];
        data.Should().NotBeEmpty();
        data.All(row => Convert.ToInt32(row[stockIndex]) <= 30).Should().BeTrue();
    }

    // --- String patterns ---

    [SkippableFact]
    public void ContainsFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_contains", "top" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.Contains("top", StringComparison.OrdinalIgnoreCase) == true).Should().BeTrue();
    }

    [SkippableFact]
    public void StartsWithFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_starts_with", "T" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.StartsWith("T", StringComparison.OrdinalIgnoreCase) == true).Should().BeTrue();
    }

    [SkippableFact]
    public void EndsWithFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_ends_with", "s" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.EndsWith("s", StringComparison.OrdinalIgnoreCase) == true).Should().BeTrue();
    }

    // --- Set membership ---

    [SkippableFact]
    public void InFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Status", new Dictionary<string, object?> { { "_in", new List<object?> { "Pending", "Shipped" } } } }
        };
        var query = BuildQuery("Orders", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Orders"].data;
        var statusIndex = results["Orders"].index["Status"];
        data.Should().NotBeEmpty();
        data.All(row =>
        {
            var status = row[statusIndex]?.ToString();
            return status == "Pending" || status == "Shipped";
        }).Should().BeTrue();
    }

    [SkippableFact]
    public void NotInFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Status", new Dictionary<string, object?> { { "_nin", new List<object?> { "Pending" } } } }
        };
        var query = BuildQuery("Orders", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Orders"].data;
        var statusIndex = results["Orders"].index["Status"];
        data.Should().NotBeEmpty();
        data.All(row => row[statusIndex]?.ToString() != "Pending").Should().BeTrue();
    }

    // --- BETWEEN ---

    [SkippableFact]
    public void BetweenFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_between", new List<object?> { 20m, 50m } } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        data.Should().NotBeEmpty();
        data.All(row =>
        {
            var price = Convert.ToDecimal(row[priceIndex]);
            return price >= 20m && price <= 50m;
        }).Should().BeTrue();
    }

    // --- NULL handling ---

    [SkippableFact]
    public void NullEqualityFilter_IsNull()
    {
        var filter = new Dictionary<string, object?>
        {
            { "City", new Dictionary<string, object?> { { "_eq", null } } }
        };
        var query = BuildQuery("Customers", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Customers"].data;
        var cityIndex = results["Customers"].index["City"];
        // All seeded customers have cities, so this should return 0
        data.All(row => row[cityIndex] == null).Should().BeTrue();
    }

    [SkippableFact]
    public void NullNotEqualFilter_IsNotNull()
    {
        var filter = new Dictionary<string, object?>
        {
            { "City", new Dictionary<string, object?> { { "_neq", null } } }
        };
        var query = BuildQuery("Customers", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Customers"].data;
        data.Should().HaveCount(TestSchema.Counts.Customers); // All have cities
    }

    // --- Logical composition ---

    [SkippableFact]
    public void AndFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "and", new List<object?>
                {
                    (object?)new Dictionary<string, object?>
                    {
                        { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
                    },
                    (object?)new Dictionary<string, object?>
                    {
                        { "Price", new Dictionary<string, object?> { { "_gt", 500m } } }
                    }
                }
            }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var catIndex = results["Products"].index["CategoryId"];
        var priceIndex = results["Products"].index["Price"];
        data.Should().NotBeEmpty();
        data.All(row =>
            Convert.ToInt32(row[catIndex]) == 1 &&
            Convert.ToDecimal(row[priceIndex]) > 500m
        ).Should().BeTrue();
    }

    [SkippableFact]
    public void OrFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "or", new List<object?>
                {
                    (object?)new Dictionary<string, object?>
                    {
                        { "Name", new Dictionary<string, object?> { { "_eq", "Laptop" } } }
                    },
                    (object?)new Dictionary<string, object?>
                    {
                        { "Name", new Dictionary<string, object?> { { "_eq", "Phone" } } }
                    }
                }
            }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().HaveCount(2);
        var names = data.Select(row => row[nameIndex]?.ToString()).ToHashSet();
        names.Should().Contain("Laptop");
        names.Should().Contain("Phone");
    }

    // --- Negated string patterns ---

    [SkippableFact]
    public void NContainsFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_ncontains", "top" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.Contains("top", StringComparison.OrdinalIgnoreCase) != true).Should().BeTrue();
    }

    [SkippableFact]
    public void NStartsWithFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_nstarts_with", "T" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.StartsWith("T", StringComparison.OrdinalIgnoreCase) != true).Should().BeTrue();
    }

    [SkippableFact]
    public void NEndsWithFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_nends_with", "s" } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var nameIndex = results["Products"].index["Name"];
        data.Should().NotBeEmpty();
        data.All(row => row[nameIndex]?.ToString()?.EndsWith("s", StringComparison.OrdinalIgnoreCase) != true).Should().BeTrue();
    }

    // --- Negated BETWEEN ---

    [SkippableFact]
    public void NBetweenFilter()
    {
        var filter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_nbetween", new List<object?> { 20m, 50m } } } }
        };
        var query = BuildQuery("Products", filter);
        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        var data = results["Products"].data;
        var priceIndex = results["Products"].index["Price"];
        data.Should().NotBeEmpty();
        data.All(row =>
        {
            var price = Convert.ToDecimal(row[priceIndex]);
            return price < 20m || price > 50m;
        }).Should().BeTrue();
    }

    // --- Operator distinction: each operator MUST return different result counts ---
    // These tests prove that operators are not silently mapping to _eq.

    [SkippableFact]
    public void IntFilter_EqVsGt_ReturnDifferentCounts()
    {
        // _eq CategoryId=1 → exactly 4 products
        var eqFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
        };
        var eqQuery = BuildQuery("Products", eqFilter);
        var eqResults = QueryExecutor.Execute(eqQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var eqCount = eqResults["Products"].data.Count;

        // _gt CategoryId>1 → all products not in category 1 (16 products)
        var gtFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_gt", 1 } } }
        };
        var gtQuery = BuildQuery("Products", gtFilter);
        var gtResults = QueryExecutor.Execute(gtQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var gtCount = gtResults["Products"].data.Count;

        eqCount.Should().Be(TestSchema.Counts.ProductsPerCategory, "_eq should return exactly one category's products");
        gtCount.Should().Be(TestSchema.Counts.Products - TestSchema.Counts.ProductsPerCategory,
            "_gt should return all products in categories 2-5");
        gtCount.Should().NotBe(eqCount, "_gt must return different results than _eq");
    }

    [SkippableFact]
    public void IntFilter_EqVsLt_ReturnDifferentCounts()
    {
        // _eq CategoryId=3 → 4 products
        var eqFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 3 } } }
        };
        var eqQuery = BuildQuery("Products", eqFilter);
        var eqResults = QueryExecutor.Execute(eqQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var eqCount = eqResults["Products"].data.Count;

        // _lt CategoryId<3 → products in categories 1 and 2 (8 products)
        var ltFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_lt", 3 } } }
        };
        var ltQuery = BuildQuery("Products", ltFilter);
        var ltResults = QueryExecutor.Execute(ltQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var ltCount = ltResults["Products"].data.Count;

        eqCount.Should().Be(TestSchema.Counts.ProductsPerCategory);
        ltCount.Should().Be(TestSchema.Counts.ProductsPerCategory * 2, "_lt 3 should return categories 1 and 2");
        ltCount.Should().NotBe(eqCount, "_lt must return different results than _eq");
    }

    [SkippableFact]
    public void IntFilter_GteVsGt_ReturnDifferentCounts()
    {
        // _gte CategoryId>=3 → categories 3,4,5 = 12 products
        var gteFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_gte", 3 } } }
        };
        var gteQuery = BuildQuery("Products", gteFilter);
        var gteResults = QueryExecutor.Execute(gteQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var gteCount = gteResults["Products"].data.Count;

        // _gt CategoryId>3 → categories 4,5 = 8 products
        var gtFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_gt", 3 } } }
        };
        var gtQuery = BuildQuery("Products", gtFilter);
        var gtResults = QueryExecutor.Execute(gtQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var gtCount = gtResults["Products"].data.Count;

        gteCount.Should().Be(TestSchema.Counts.ProductsPerCategory * 3);
        gtCount.Should().Be(TestSchema.Counts.ProductsPerCategory * 2);
        gteCount.Should().BeGreaterThan(gtCount, "_gte must include the boundary value that _gt excludes");
    }

    [SkippableFact]
    public void IntFilter_LteVsLt_ReturnDifferentCounts()
    {
        // _lte CategoryId<=2 → categories 1,2 = 8 products
        var lteFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_lte", 2 } } }
        };
        var lteQuery = BuildQuery("Products", lteFilter);
        var lteResults = QueryExecutor.Execute(lteQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var lteCount = lteResults["Products"].data.Count;

        // _lt CategoryId<2 → category 1 only = 4 products
        var ltFilter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_lt", 2 } } }
        };
        var ltQuery = BuildQuery("Products", ltFilter);
        var ltResults = QueryExecutor.Execute(ltQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var ltCount = ltResults["Products"].data.Count;

        lteCount.Should().Be(TestSchema.Counts.ProductsPerCategory * 2);
        ltCount.Should().Be(TestSchema.Counts.ProductsPerCategory);
        lteCount.Should().BeGreaterThan(ltCount, "_lte must include the boundary value that _lt excludes");
    }

    [SkippableFact]
    public void DecimalFilter_EqVsGt_ReturnDifferentCounts()
    {
        // _eq Price=999.99 → exactly 1 product (Laptop)
        var eqFilter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_eq", 999.99m } } }
        };
        var eqQuery = BuildQuery("Products", eqFilter);
        var eqResults = QueryExecutor.Execute(eqQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var eqCount = eqResults["Products"].data.Count;

        // _gt Price>999.99 → 0 products (nothing more expensive)
        var gtFilter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_gt", 999.99m } } }
        };
        var gtQuery = BuildQuery("Products", gtFilter);
        var gtResults = QueryExecutor.Execute(gtQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var gtCount = gtResults["Products"].data.Count;

        eqCount.Should().Be(1, "only one product costs exactly 999.99");
        gtCount.Should().Be(0, "no product costs more than 999.99");
        gtCount.Should().NotBe(eqCount, "_gt must return different results than _eq");
    }

    [SkippableFact]
    public void StringFilter_ContainsVsNContains_AreComplementary()
    {
        var containsFilter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_contains", "a" } } }
        };
        var containsQuery = BuildQuery("Products", containsFilter);
        var containsResults = QueryExecutor.Execute(containsQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var containsCount = containsResults["Products"].data.Count;

        var ncontainsFilter = new Dictionary<string, object?>
        {
            { "Name", new Dictionary<string, object?> { { "_ncontains", "a" } } }
        };
        var ncontainsQuery = BuildQuery("Products", ncontainsFilter);
        var ncontainsResults = QueryExecutor.Execute(ncontainsQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var ncontainsCount = ncontainsResults["Products"].data.Count;

        containsCount.Should().BeGreaterThan(0);
        ncontainsCount.Should().BeGreaterThan(0);
        (containsCount + ncontainsCount).Should().Be(TestSchema.Counts.Products,
            "_contains and _ncontains must be complementary (cover all rows)");
    }

    [SkippableFact]
    public void BetweenVsNBetween_AreComplementary()
    {
        var betweenFilter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_between", new List<object?> { 20m, 50m } } } }
        };
        var betweenQuery = BuildQuery("Products", betweenFilter);
        var betweenResults = QueryExecutor.Execute(betweenQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var betweenCount = betweenResults["Products"].data.Count;

        var nbetweenFilter = new Dictionary<string, object?>
        {
            { "Price", new Dictionary<string, object?> { { "_nbetween", new List<object?> { 20m, 50m } } } }
        };
        var nbetweenQuery = BuildQuery("Products", nbetweenFilter);
        var nbetweenResults = QueryExecutor.Execute(nbetweenQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);
        var nbetweenCount = nbetweenResults["Products"].data.Count;

        betweenCount.Should().BeGreaterThan(0);
        nbetweenCount.Should().BeGreaterThan(0);
        (betweenCount + nbetweenCount).Should().Be(TestSchema.Counts.Products,
            "_between and _nbetween must be complementary (cover all rows)");
    }

    // --- Filter with pagination ---

    [SkippableFact]
    public void FilterWithPagination()
    {
        // Filter: CategoryId = 1, then paginate
        var filter = new Dictionary<string, object?>
        {
            { "CategoryId", new Dictionary<string, object?> { { "_eq", 1 } } }
        };
        var query = BuildQuery("Products", filter);
        query.Limit = 2;
        query.Offset = 0;
        query.Sort = new List<string> { "Id_asc" };
        query.IncludeResult = true;

        var results = QueryExecutor.Execute(query, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results["Products"].data.Should().HaveCount(2);
        var countData = results["Products=>count"].data;
        Convert.ToInt32(countData[0][0]).Should().Be(TestSchema.Counts.ProductsPerCategory);
    }
}

// Concrete test classes per dialect
public sealed class SqliteFilteringTests : FilteringTestBase<SqliteTestDatabase>
{
    public SqliteFilteringTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}
