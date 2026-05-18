using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
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
        _connectionString = $"Data Source=bifrost_full_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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

    // Top-level table queries return `<table>_paged { data: [...] total offset limit }`,
    // so test selections wrap the column list in `data { ... }` and extractions
    // hop through ["data"] before deserializing the row list.

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = "query { products { data { productId name price stock } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_FilterByPrice_ShouldReturnMatchingProducts()
    {
        var query = "query { products(filter: { price: { _lt: 50 } }) { data { name price } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => double.Parse(p["price"].ToString()!) < 50).Should().BeTrue();
    }

    [Fact]
    public async Task Query_SortByPrice_ShouldReturnSortedProducts()
    {
        // `sort` is a list of `<table>SortEnum` values (e.g. `price_asc`),
        // not an object literal — schema generator emits one enum per column.
        var query = "query { products(sort: [price_asc]) { data { name price } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        var prices = products!.Select(p => double.Parse(p["price"].ToString()!)).ToList();
        prices.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Query_WithPagination_ShouldReturnCorrectPage()
    {
        var query = "query { products(sort: [productId_asc], offset: 1, limit: 2) { data { productId name } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_ProductWithCategory_ShouldReturnJoinedData()
    {
        // Single-link FK joins (forward FK) use the parent table's GraphQL
        // name, which is the pluralized table name — `categories`, not `category`.
        var query = "query { products(filter: { productId: { _eq: 1 } }) { data { name categories { name description } } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        var category = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(products![0]["categories"]));
        category!["name"].ToString().Should().Be("Electronics");
    }

    [Fact]
    public async Task Query_CategoryWithProducts_ShouldReturnOneToMany()
    {
        var query = "query { categories(filter: { categoryId: { _eq: 1 } }) { data { name products { name price } } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");

        // Reverse-FK collection joins (MultiLinks) return a flat `[Type]` list,
        // not a paged wrapper, so no extra hop is needed.
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(categories![0]["products"]));
        products.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Query_OrderWithNestedJoins_ShouldReturnComplexData()
    {
        var query = @"query {
            orders(filter: { orderId: { _eq: 1 } }) {
                data {
                    orderDate
                    customers { name email }
                    orderItems {
                        quantity
                        products { name categories { name } }
                    }
                }
            }
        }";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = UnwrapPagedRows(result, "orders");

        var customer = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(orders![0]["customers"]));
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
        var insertedId = GetMutationScalar(result, "products");
        insertedId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Mutation_UpdateProduct_ShouldModifyRecord()
    {
        // Update input type repeats every non-null column as required (schema
        // generator does not distinguish update from insert nullability), so
        // tests must resupply name/categoryId even when only price changes.
        var mutation = @"mutation { products(update: { productId: 1, categoryId: 1, name: ""Laptop"", price: 899.99, stock: 15 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        var verifyQuery = "query { products(filter: { productId: { _eq: 1 } }) { data { price stock } } }";
        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = UnwrapPagedRows(verifyResult, "products");

        double.Parse(products![0]["price"].ToString()!).Should().BeApproximately(899.99, 0.01);
        int.Parse(products[0]["stock"].ToString()!).Should().Be(15);
    }

    [Fact]
    public async Task Mutation_DeleteProduct_ShouldRemoveRecord()
    {
        // Seeded products 1-3 are referenced by OrderItems, so deleting them
        // violates the FK constraint. Insert a fresh, unreferenced row and
        // delete that to exercise the delete path cleanly.
        var insert = @"mutation { products(insert: { categoryId: 1, name: ""Disposable"", price: 1.00, stock: 1 }) }";
        var insertResult = await ExecuteQueryAsync(insert);
        insertResult.Errors.Should().BeNullOrEmpty();
        var newId = GetMutationScalar(insertResult, "products");

        var mutation = $"mutation {{ products(delete: {{ productId: {newId} }}) }}";
        var result = await ExecuteQueryAsync(mutation);
        result.Errors.Should().BeNullOrEmpty();

        var verifyQuery = $"query {{ products(filter: {{ productId: {{ _eq: {newId} }} }}) {{ data {{ productId }} }} }}";
        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = UnwrapPagedRows(verifyResult, "products");

        products.Should().BeEmpty();
    }

    // _agg coverage — exercises the nested-join aggregate against real Sqlite.
    // Seeded: Electronics(1) has Laptop+Mouse, Books(2) has Book — counts 2/1.
    // Prices: Electronics 999.99+29.99 (sum 1029.98, avg 514.99); Books 49.99.

    [Fact]
    public async Task Aggregate_NestedJoinCount_ShouldReturnTotal()
    {
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        _agg(value: { products: { column: productId } } operation: Count)
                    }
                }
            }
        ";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");
        var byId = categories!.ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().Be(2);
        double.Parse(byId[2]["_agg"].ToString()!).Should().Be(1);
    }

    [Fact]
    public async Task Aggregate_NestedJoinSum_ShouldReturnTotalPrice()
    {
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        _agg(value: { products: { column: price } } operation: Sum)
                    }
                }
            }
        ";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");
        var byId = categories!.ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(1029.98, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
    }

    [Fact]
    public async Task Aggregate_NestedJoinAvg_ShouldReturnMeanPrice()
    {
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        _agg(value: { products: { column: price } } operation: Avg)
                    }
                }
            }
        ";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");
        var byId = categories!.ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(514.99, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
    }

    [Fact]
    public async Task Aggregate_NestedJoinMin_ShouldReturnMinPrice()
    {
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        _agg(value: { products: { column: price } } operation: Min)
                    }
                }
            }
        ";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var byId = UnwrapPagedRows(result, "categories")!
            .ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        // Electronics: Laptop 999.99, Mouse 29.99 -> min 29.99; Books: Book 49.99
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(29.99, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
    }

    [Fact]
    public async Task Aggregate_NestedJoinMax_ShouldReturnMaxPrice()
    {
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        _agg(value: { products: { column: price } } operation: Max)
                    }
                }
            }
        ";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var byId = UnwrapPagedRows(result, "categories")!
            .ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(999.99, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
    }

    // GraphQL .NET returns ExecutionResult.Data as a RootExecutionNode whose
    // JSON shape can only be produced by GraphQLSerializer (System.Text.Json
    // cannot walk the node tree directly). The serializer writes the standard
    // `{ "data": { ... } }` envelope, which we then deserialize and navigate.
    private static List<Dictionary<string, object>>? UnwrapPagedRows(ExecutionResult result, string field)
    {
        var dataElement = SerializeDataElement(result);
        var paged = dataElement.GetProperty(field);
        return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(paged.GetProperty("data").GetRawText());
    }

    private static int GetMutationScalar(ExecutionResult result, string field)
    {
        var dataElement = SerializeDataElement(result);
        return dataElement.GetProperty(field).GetInt32();
    }

    private static JsonElement SerializeDataElement(ExecutionResult result)
    {
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").Clone();
    }
}
