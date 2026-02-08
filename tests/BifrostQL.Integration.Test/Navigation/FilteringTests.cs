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
