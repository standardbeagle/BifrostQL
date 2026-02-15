using BifrostQL.Core.Model;
using FluentAssertions;
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
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")
            ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

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

    #region Basic Queries

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = @"
            query {
                products {
                    productId
                    name
                    price
                    stock
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(5);
    }

    [Fact]
    public async Task Query_SingleProduct_ShouldReturnCorrectProduct()
    {
        var query = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    productId
                    name
                    price
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(1);
        products![0]["name"].ToString().Should().Be("Laptop");
    }

    #endregion

    #region Filtering

    [Fact]
    public async Task Query_FilterByPrice_ShouldReturnMatchingProducts()
    {
        var query = @"
            query {
                products(filter: { price: { _lt: 50 } }) {
                    name
                    price
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => decimal.Parse(p["price"].ToString()!) < 50).Should().BeTrue();
    }

    [Fact]
    public async Task Query_FilterByMultipleConditions_ShouldReturnMatchingProducts()
    {
        var query = @"
            query {
                products(filter: {
                    _and: [
                        { categoryId: { _eq: 1 } },
                        { isActive: { _eq: true } }
                    ]
                }) {
                    name
                    categoryId
                    isActive
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCountGreaterThan(0);
        products!.All(p => int.Parse(p["categoryId"].ToString()!) == 1).Should().BeTrue();
    }

    #endregion

    #region Sorting

    [Fact]
    public async Task Query_SortByPrice_ShouldReturnSortedProducts()
    {
        var query = @"
            query {
                products(sort: { price: asc }) {
                    name
                    price
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        var prices = products!.Select(p => decimal.Parse(p["price"].ToString()!)).ToList();
        prices.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Query_SortByNameDescending_ShouldReturnSortedProducts()
    {
        var query = @"
            query {
                products(sort: { name: desc }) {
                    name
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        var names = products!.Select(p => p["name"].ToString()!).ToList();
        names.Should().BeInDescendingOrder();
    }

    #endregion

    #region Pagination

    [Fact]
    public async Task Query_WithLimit_ShouldReturnLimitedResults()
    {
        var query = @"
            query {
                products(limit: 2) {
                    productId
                    name
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_WithOffsetAndLimit_ShouldReturnCorrectPage()
    {
        var query = @"
            query {
                products(sort: { productId: asc }, offset: 2, limit: 2) {
                    productId
                    name
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(2);
        int.Parse(products![0]["productId"].ToString()!).Should().Be(3);
    }

    #endregion

    #region Joins - Single Links

    [Fact]
    public async Task Query_ProductWithCategory_ShouldReturnJoinedData()
    {
        var query = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    name
                    category {
                        name
                        description
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(1);
        var category = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(products![0]["category"]));
        category!["name"].ToString().Should().Be("Electronics");
    }

    [Fact]
    public async Task Query_OrderWithCustomer_ShouldReturnJoinedData()
    {
        var query = @"
            query {
                orders(filter: { orderId: { _eq: 1 } }) {
                    orderDate
                    totalAmount
                    customer {
                        name
                        email
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["orders"]));

        var customer = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(orders![0]["customer"]));
        customer!["name"].ToString().Should().Be("John Doe");
    }

    #endregion

    #region Joins - Multi Links

    [Fact]
    public async Task Query_CategoryWithProducts_ShouldReturnOneToMany()
    {
        var query = @"
            query {
                categories(filter: { categoryId: { _eq: 1 } }) {
                    name
                    products {
                        name
                        price
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var categories = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["categories"]));

        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(categories![0]["products"]));
        products.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task Query_OrderWithItems_ShouldReturnNestedData()
    {
        var query = @"
            query {
                orders(filter: { orderId: { _eq: 1 } }) {
                    orderDate
                    orderItems {
                        quantity
                        unitPrice
                        product {
                            name
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["orders"]));

        var items = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(orders![0]["orderItems"]));
        items.Should().HaveCount(2);
    }

    #endregion

    #region Mutations - Insert

    [Fact]
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
        var insertedId = int.Parse(result.Data.ToString()!);
        insertedId.Should().BeGreaterThan(0);

        // Verify product was created
        var verifyQuery = $@"
            query {{
                products(filter: {{ productId: {{ _eq: {insertedId} }} }}) {{
                    name
                    price
                }}
            }}
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)verifyResult.Data!)["products"]));

        products!.Should().HaveCount(1);
        products[0]["name"].ToString().Should().Be("New Product");
    }

    #endregion

    #region Mutations - Update

    [Fact]
    public async Task Mutation_UpdateProduct_ShouldModifyRecord()
    {
        var mutation = @"
            mutation {
                products(update: {
                    productId: 1,
                    price: 899.99,
                    stock: 15
                })
            }
        ";

        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();

        // Verify update
        var verifyQuery = @"
            query {
                products(filter: { productId: { _eq: 1 } }) {
                    price
                    stock
                }
            }
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)verifyResult.Data!)["products"]));

        decimal.Parse(products![0]["price"].ToString()!).Should().Be(899.99m);
        int.Parse(products[0]["stock"].ToString()!).Should().Be(15);
    }

    #endregion

    #region Mutations - Delete

    [Fact]
    public async Task Mutation_DeleteProduct_ShouldRemoveRecord()
    {
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
                    productId
                }
            }
        ";

        var verifyResult = await ExecuteQueryAsync(verifyQuery);
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)verifyResult.Data!)["products"]));

        products.Should().BeEmpty();
    }

    #endregion

    #region Complex Queries

    [Fact]
    public async Task Query_ComplexFilterSortJoin_ShouldReturnCorrectData()
    {
        var query = @"
            query {
                orders(
                    filter: {
                        _and: [
                            { totalAmount: { _gt: 20 } },
                            { status: { _in: [""Delivered"", ""Shipped""] } }
                        ]
                    },
                    sort: { orderDate: desc }
                ) {
                    orderDate
                    totalAmount
                    status
                    customer {
                        name
                        country
                    }
                    orderItems {
                        quantity
                        product {
                            name
                            category {
                                name
                            }
                        }
                    }
                }
            }
        ";

        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var orders = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["orders"]));

        orders.Should().HaveCountGreaterThan(0);
        orders!.All(o => decimal.Parse(o["totalAmount"].ToString()!) > 20).Should().BeTrue();
    }

    #endregion
}
