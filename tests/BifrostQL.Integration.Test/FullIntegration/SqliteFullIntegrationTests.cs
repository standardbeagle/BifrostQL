using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Full integration tests for SQLite with dynamically loaded schema.
/// Always runs (in-memory database).
/// </summary>
[Collection("SqliteFullIntegration")]
public class SqliteFullIntegrationTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private SqliteConnection? _keepAliveConnection;

    public async Task InitializeAsync()
    {
        _connectionString = "Data Source=bifrost_full_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        await _keepAliveConnection.OpenAsync();

        var factory = new SqliteDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();

        if (_keepAliveConnection != null)
        {
            await _keepAliveConnection.DisposeAsync();
        }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            @"CREATE TABLE Categories (
                CategoryId INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Description TEXT NULL
            )",
            @"CREATE TABLE Products (
                ProductId INTEGER PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Price REAL NOT NULL,
                Stock INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId)
            )",
            @"CREATE TABLE Customers (
                CustomerId INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                City TEXT NULL
            )",
            @"CREATE TABLE Orders (
                OrderId INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomerId INTEGER NOT NULL,
                OrderDate TEXT NOT NULL,
                TotalAmount REAL NOT NULL,
                Status TEXT NOT NULL,
                FOREIGN KEY (CustomerId) REFERENCES Customers(CustomerId)
            )",
            @"CREATE TABLE OrderItems (
                OrderItemId INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId INTEGER NOT NULL,
                ProductId INTEGER NOT NULL,
                Quantity INTEGER NOT NULL,
                UnitPrice REAL NOT NULL,
                FOREIGN KEY (OrderId) REFERENCES Orders(OrderId),
                FOREIGN KEY (ProductId) REFERENCES Products(ProductId)
            )"
        };

        foreach (var statement in statements)
        {
            var cmd = new SqliteCommand(statement, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "INSERT INTO Categories (Name, Description) VALUES ('Electronics', 'Electronic devices'), ('Books', 'Physical books')",
            "INSERT INTO Products (CategoryId, Name, Price, Stock) VALUES (1, 'Laptop', 999.99, 10), (1, 'Mouse', 29.99, 50), (2, 'Book', 49.99, 20)",
            "INSERT INTO Customers (Name, Email, City) VALUES ('John Doe', 'john@example.com', 'New York'), ('Jane Smith', 'jane@example.com', 'London')",
            "INSERT INTO Orders (CustomerId, OrderDate, TotalAmount, Status) VALUES (1, '2024-01-15', 1029.98, 'Delivered'), (2, '2024-02-10', 49.99, 'Shipped')",
            "INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (1, 1, 1, 999.99), (1, 2, 1, 29.99), (2, 3, 1, 49.99)"
        };

        foreach (var statement in statements)
        {
            var cmd = new SqliteCommand(statement, (SqliteConnection)conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = "query { products { productId name price stock } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_FilterByPrice_ShouldReturnMatchingProducts()
    {
        var query = "query { products(filter: { price: { _lt: 50 } }) { name price } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => double.Parse(p["price"].ToString()!) < 50).Should().BeTrue();
    }

    [Fact]
    public async Task Query_SortByPrice_ShouldReturnSortedProducts()
    {
        var query = "query { products(sort: { price: asc }) { name price } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        var prices = products!.Select(p => double.Parse(p["price"].ToString()!)).ToList();
        prices.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Query_WithPagination_ShouldReturnCorrectPage()
    {
        var query = "query { products(sort: { productId: asc }, offset: 1, limit: 2) { productId name } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_ProductWithCategory_ShouldReturnJoinedData()
    {
        var query = "query { products(filter: { productId: { _eq: 1 } }) { name category { name description } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        var category = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(products![0]["category"]));
        category!["name"].ToString().Should().Be("Electronics");
    }

    [Fact]
    public async Task Query_CategoryWithProducts_ShouldReturnOneToMany()
    {
        var query = "query { categories(filter: { categoryId: { _eq: 1 } }) { name products { name price } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["categories"]));

        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(categories![0]["products"]));
        products.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Query_OrderWithNestedJoins_ShouldReturnComplexData()
    {
        var query = @"query {
            orders(filter: { orderId: { _eq: 1 } }) {
                orderDate
                customer { name email }
                orderItems {
                    quantity
                    product { name category { name } }
                }
            }
        }";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["orders"]));

        var customer = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(orders![0]["customer"]));
        customer!["name"].ToString().Should().Be("John Doe");

        var items = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(orders[0]["orderItems"]));
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Mutation_InsertProduct_ShouldCreateNewRecord()
    {
        var mutation = @"mutation { products(insert: { categoryId: 1, name: ""New Product"", price: 99.99, stock: 5 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();
        var insertedId = int.Parse(result.Data.ToString()!);
        insertedId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Mutation_UpdateProduct_ShouldModifyRecord()
    {
        var mutation = @"mutation { products(update: { productId: 1, price: 899.99, stock: 15 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        // Verify
        var verifyQuery = "query { products(filter: { productId: { _eq: 1 } }) { price stock } }";
        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)verifyResult.Data!)["products"]));

        double.Parse(products![0]["price"].ToString()!).Should().BeApproximately(899.99, 0.01);
        int.Parse(products[0]["stock"].ToString()!).Should().Be(15);
    }

    [Fact]
    public async Task Mutation_DeleteProduct_ShouldRemoveRecord()
    {
        var mutation = @"mutation { products(delete: { productId: 3 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        // Verify deletion
        var verifyQuery = "query { products(filter: { productId: { _eq: 3 } }) { productId } }";
        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)verifyResult.Data!)["products"]));

        products.Should().BeEmpty();
    }
}
