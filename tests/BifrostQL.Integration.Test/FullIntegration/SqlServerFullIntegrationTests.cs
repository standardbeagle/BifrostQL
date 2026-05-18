using BifrostQL.Core.Model;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Full integration tests for SQL Server with dynamically loaded schema.
/// Tests queries, mutations, joins, filtering, sorting, pagination end-to-end.
/// </summary>
[Collection("SqlServerFullIntegration")]
public class SqlServerFullIntegrationTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        if (masterConnString == null)
        {
            // Skip the entire SQL Server suite when no server is available
            // (mirrors the MySQL/Postgres conventions for env-gated suites).
            Skip.If(true, "BIFROST_TEST_SQLSERVER environment variable not set");
            return;
        }

        _testDbName = $"BifrostFullInt_{Guid.NewGuid():N}";

        // Create database
        await using var masterConn = new SqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new SqlCommand($"CREATE DATABASE [{_testDbName}]", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new SqlConnectionStringBuilder(masterConnString) { InitialCatalog = _testDbName };
        _connectionString = builder.ConnectionString;

        var factory = new DbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();

        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")
                ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";
            await using var conn = new SqlConnection(masterConnString);
            await conn.OpenAsync();
            var dropCmd = new SqlCommand($"DROP DATABASE IF EXISTS [{_testDbName}]", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var ddl = @"
CREATE TABLE Categories (
    CategoryId INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(50) NOT NULL,
    Description NVARCHAR(200) NULL
);

CREATE TABLE Products (
    ProductId INT IDENTITY(1,1) PRIMARY KEY,
    CategoryId INT NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Price DECIMAL(10,2) NOT NULL,
    Stock INT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId)
);

CREATE TABLE Customers (
    CustomerId INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Email NVARCHAR(100) NOT NULL,
    City NVARCHAR(50) NULL,
    Country NVARCHAR(50) NOT NULL
);

CREATE TABLE Orders (
    OrderId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId INT NOT NULL,
    OrderDate DATETIME2 NOT NULL,
    TotalAmount DECIMAL(10,2) NOT NULL,
    Status NVARCHAR(20) NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES Customers(CustomerId)
);

CREATE TABLE OrderItems (
    OrderItemId INT IDENTITY(1,1) PRIMARY KEY,
    OrderId INT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(10,2) NOT NULL,
    CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES Orders(OrderId),
    CONSTRAINT FK_OrderItems_Products FOREIGN KEY (ProductId) REFERENCES Products(ProductId)
);
";

        var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var seed = @"
-- Categories
INSERT INTO Categories (Name, Description) VALUES ('Electronics', 'Electronic devices and gadgets');
INSERT INTO Categories (Name, Description) VALUES ('Books', 'Physical and digital books');
INSERT INTO Categories (Name, Description) VALUES ('Clothing', 'Apparel and accessories');

-- Products
INSERT INTO Products (CategoryId, Name, Price, Stock, IsActive) VALUES (1, 'Laptop', 999.99, 10, 1);
INSERT INTO Products (CategoryId, Name, Price, Stock, IsActive) VALUES (1, 'Mouse', 29.99, 50, 1);
INSERT INTO Products (CategoryId, Name, Price, Stock, IsActive) VALUES (2, 'Programming Book', 49.99, 20, 1);
INSERT INTO Products (CategoryId, Name, Price, Stock, IsActive) VALUES (3, 'T-Shirt', 19.99, 100, 1);
INSERT INTO Products (CategoryId, Name, Price, Stock, IsActive) VALUES (1, 'Keyboard', 79.99, 0, 0);

-- Customers
INSERT INTO Customers (Name, Email, City, Country) VALUES ('John Doe', 'john@example.com', 'New York', 'USA');
INSERT INTO Customers (Name, Email, City, Country) VALUES ('Jane Smith', 'jane@example.com', 'London', 'UK');
INSERT INTO Customers (Name, Email, City, Country) VALUES ('Bob Johnson', 'bob@example.com', 'Toronto', 'Canada');

-- Orders
INSERT INTO Orders (CustomerId, OrderDate, TotalAmount, Status) VALUES (1, '2024-01-15', 1029.98, 'Delivered');
INSERT INTO Orders (CustomerId, OrderDate, TotalAmount, Status) VALUES (2, '2024-02-10', 49.99, 'Shipped');
INSERT INTO Orders (CustomerId, OrderDate, TotalAmount, Status) VALUES (1, '2024-02-20', 19.99, 'Pending');

-- OrderItems
INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (1, 1, 1, 999.99);
INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (1, 2, 1, 29.99);
INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (2, 3, 1, 49.99);
INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES (3, 4, 1, 19.99);
";

        var cmd = conn.CreateCommand();
        cmd.CommandText = seed;
        await cmd.ExecuteNonQueryAsync();
    }

    // Top-level table queries return `<table>_paged { data: [...] total offset limit }`,
    // so test selections wrap the column list in `data { ... }` and extractions
    // hop through ["data"] before deserializing the row list.

    #region Basic Queries

    [SkippableFact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = @"
            query {
                products {
                    data {
                        productId
                        name
                        price
                        stock
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(5);
    }

    [SkippableFact]
    public async Task Query_SingleProduct_ShouldReturnCorrectProduct()
    {
        var query = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    data {
                        productId
                        name
                        price
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(1);
        products![0]["name"].ToString().Should().Be("Laptop");
    }

    #endregion

    #region Filtering

    [SkippableFact]
    public async Task Query_FilterByPrice_ShouldReturnMatchingProducts()
    {
        var query = @"
            query {
                products(filter: { price: { _lt: 50 } }) {
                    data {
                        name
                        price
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => decimal.Parse(p["price"].ToString()!) < 50).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Query_FilterByMultipleConditions_ShouldReturnMatchingProducts()
    {
        var query = @"
            query {
                products(filter: {
                    and: [
                        { categoryId: { _eq: 1 } },
                        { isActive: { _eq: true } }
                    ]
                }) {
                    data {
                        name
                        categoryId
                        isActive
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => int.Parse(p["categoryId"].ToString()!) == 1).Should().BeTrue();
    }

    #endregion

    #region Sorting

    [SkippableFact]
    public async Task Query_SortByPrice_ShouldReturnSortedProducts()
    {
        // `sort` is a list of `<table>SortEnum` values (e.g. `price_asc`),
        // not an object literal — schema generator emits one enum per column.
        var query = @"
            query {
                products(sort: [price_asc]) {
                    data {
                        name
                        price
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        var prices = products!.Select(p => decimal.Parse(p["price"].ToString()!)).ToList();
        prices.Should().BeInAscendingOrder();
    }

    [SkippableFact]
    public async Task Query_SortByNameDescending_ShouldReturnSortedProducts()
    {
        var query = @"
            query {
                products(sort: [name_desc]) {
                    data {
                        name
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        var names = products!.Select(p => p["name"].ToString()!).ToList();
        names.Should().BeInDescendingOrder();
    }

    #endregion

    #region Pagination

    [SkippableFact]
    public async Task Query_WithLimit_ShouldReturnLimitedResults()
    {
        var query = @"
            query {
                products(limit: 2) {
                    data {
                        productId
                        name
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Query_WithOffsetAndLimit_ShouldReturnCorrectPage()
    {
        var query = @"
            query {
                products(sort: [productId_asc], offset: 2, limit: 2) {
                    data {
                        productId
                        name
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(2);
        int.Parse(products![0]["productId"].ToString()!).Should().Be(3);
    }

    #endregion

    #region Joins - Single Links

    [SkippableFact]
    public async Task Query_ProductWithCategory_ShouldReturnJoinedData()
    {
        // Single-link FK joins (forward FK) use the parent table's GraphQL
        // name, which is the pluralized table name — `categories`, not `category`.
        var query = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    data {
                        name
                        categories {
                            name
                            description
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = UnwrapPagedRows(result, "products");

        products.Should().HaveCount(1);
        var category = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(products![0]["categories"]));
        category!["name"].ToString().Should().Be("Electronics");
    }

    [SkippableFact]
    public async Task Query_OrderWithCustomer_ShouldReturnJoinedData()
    {
        var query = @"
            query {
                orders(filter: { orderId: { _eq: 1 } }) {
                    data {
                        orderDate
                        totalAmount
                        customers {
                            name
                            email
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = UnwrapPagedRows(result, "orders");

        var customer = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(orders![0]["customers"]));
        customer!["name"].ToString().Should().Be("John Doe");
    }

    #endregion

    #region Joins - Multi Links

    [SkippableFact]
    public async Task Query_CategoryWithProducts_ShouldReturnOneToMany()
    {
        var query = @"
            query {
                categories(filter: { categoryId: { _eq: 1 } }) {
                    data {
                        name
                        products {
                            name
                            price
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");

        // Reverse-FK collection joins (MultiLinks) return a flat `[Type]` list,
        // not a paged wrapper, so no extra hop is needed.
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(categories![0]["products"]));
        products.Should().HaveCountGreaterThan(0);
    }

    [SkippableFact]
    public async Task Aggregate_NestedJoinCount_ShouldReturnTotal()
    {
        // RED: verifies `_agg(value: { products: productId } operation: count)`
        // returns the correct count of products per category. Seeded categories:
        //   1 Electronics => 3 products (Laptop, Mouse, Keyboard)
        //   2 Books       => 1 product  (Programming Book)
        //   3 Clothing    => 1 product  (T-Shirt)
        var query = @"
            query {
                categories {
                    data {
                        categoryId
                        name
                        _agg(value: { products: { column: productId } } operation: Count)
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = UnwrapPagedRows(result, "categories");
        categories.Should().NotBeNull();

        var byId = categories!.ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().Be(3, "Electronics has 3 seeded products");
        double.Parse(byId[2]["_agg"].ToString()!).Should().Be(1, "Books has 1 seeded product");
        double.Parse(byId[3]["_agg"].ToString()!).Should().Be(1, "Clothing has 1 seeded product");
    }

    [SkippableFact]
    public async Task Aggregate_NestedJoinSum_ShouldReturnTotalPrice()
    {
        // Sum prices per category: Electronics 999.99+29.99+79.99=1109.97,
        // Books 49.99, Clothing 19.99.
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
        var byId = UnwrapPagedRows(result, "categories")!
            .ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(1109.97, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
        double.Parse(byId[3]["_agg"].ToString()!).Should().BeApproximately(19.99, 0.01);
    }

    [SkippableFact]
    public async Task Aggregate_NestedJoinAvg_ShouldReturnMeanPrice()
    {
        // Avg prices: Electronics 1109.97/3 = 369.99, Books 49.99, Clothing 19.99.
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
        var byId = UnwrapPagedRows(result, "categories")!
            .ToDictionary(c => int.Parse(c["categoryId"].ToString()!));
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(369.99, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
        double.Parse(byId[3]["_agg"].ToString()!).Should().BeApproximately(19.99, 0.01);
    }

    [SkippableFact]
    public async Task Aggregate_NestedJoinMin_ShouldReturnMinPrice()
    {
        // Min prices: Electronics (Mouse 29.99), Books 49.99, Clothing 19.99.
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
        double.Parse(byId[1]["_agg"].ToString()!).Should().BeApproximately(29.99, 0.01);
        double.Parse(byId[2]["_agg"].ToString()!).Should().BeApproximately(49.99, 0.01);
        double.Parse(byId[3]["_agg"].ToString()!).Should().BeApproximately(19.99, 0.01);
    }

    [SkippableFact]
    public async Task Aggregate_NestedJoinMax_ShouldReturnMaxPrice()
    {
        // Max prices: Electronics 999.99 (Laptop), Books 49.99, Clothing 19.99.
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
        double.Parse(byId[3]["_agg"].ToString()!).Should().BeApproximately(19.99, 0.01);
    }

    [SkippableFact]
    public async Task Query_OrderWithItems_ShouldReturnNestedData()
    {
        var query = @"
            query {
                orders(filter: { orderId: { _eq: 1 } }) {
                    data {
                        orderDate
                        orderItems {
                            quantity
                            unitPrice
                            products {
                                name
                            }
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = UnwrapPagedRows(result, "orders");

        var items = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(orders![0]["orderItems"]));
        items.Should().HaveCount(2);
    }

    #endregion

    #region Mutations - Insert

    [SkippableFact]
    public async Task Mutation_InsertProduct_ShouldCreateNewRecord()
    {
        var mutation = @"
            mutation {
                products(insert: {
                    categoryId: 1,
                    name: ""New Product"",
                    price: 99.99,
                    stock: 5,
                    isActive: true
                })
            }
        ";

        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();
        var insertedId = GetMutationScalar(result, "products");
        insertedId.Should().BeGreaterThan(0);

        // Verify product was created
        var verifyQuery = $@"
            query {{
                products(filter: {{ productId: {{ _eq: {insertedId} }} }}) {{
                    data {{
                        name
                        price
                    }}
                }}
            }}
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = UnwrapPagedRows(verifyResult, "products");

        products!.Should().HaveCount(1);
        products[0]["name"].ToString().Should().Be("New Product");
    }

    #endregion

    #region Mutations - Update

    [SkippableFact]
    public async Task Mutation_UpdateProduct_ShouldModifyRecord()
    {
        // Update input type repeats every non-null column as required (schema
        // generator does not distinguish update from insert nullability), so
        // tests must resupply name/categoryId/isActive even when only price changes.
        var mutation = @"
            mutation {
                products(update: {
                    productId: 1,
                    categoryId: 1,
                    name: ""Laptop"",
                    price: 899.99,
                    stock: 15,
                    isActive: true
                })
            }
        ";

        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        // Verify update
        var verifyQuery = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    data {
                        price
                        stock
                    }
                }
            }
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = UnwrapPagedRows(verifyResult, "products");

        decimal.Parse(products![0]["price"].ToString()!).Should().Be(899.99m);
        int.Parse(products[0]["stock"].ToString()!).Should().Be(15);
    }

    #endregion

    #region Mutations - Delete

    [SkippableFact]
    public async Task Mutation_DeleteProduct_ShouldRemoveRecord()
    {
        // Product 5 ("Keyboard") is seeded but has no OrderItem reference,
        // so it can be deleted without violating the FK constraint.
        var mutation = @"
            mutation {
                products(delete: {
                    productId: 5
                })
            }
        ";

        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        // Verify deletion
        var verifyQuery = @"
            query {
                products(filter: { productId: { _eq: 5 } }) {
                    data {
                        productId
                    }
                }
            }
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = UnwrapPagedRows(verifyResult, "products");

        products.Should().BeEmpty();
    }

    #endregion

    #region Complex Queries

    [SkippableFact]
    public async Task Query_ComplexFilterSortJoin_ShouldReturnCorrectData()
    {
        var query = @"
            query {
                orders(
                    filter: {
                        and: [
                            { totalAmount: { _gt: 20 } },
                            { status: { _in: [""Delivered"", ""Shipped""] } }
                        ]
                    },
                    sort: [orderDate_desc]
                ) {
                    data {
                        orderDate
                        totalAmount
                        status
                        customers {
                            name
                            country
                        }
                        orderItems {
                            quantity
                            products {
                                name
                                categories {
                                    name
                                }
                            }
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = UnwrapPagedRows(result, "orders");

        orders.Should().HaveCountGreaterThan(0);
        orders!.All(o => decimal.Parse(o["totalAmount"].ToString()!) > 20).Should().BeTrue();
    }

    #endregion

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
