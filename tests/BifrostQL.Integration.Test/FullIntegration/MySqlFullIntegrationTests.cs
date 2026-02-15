using BifrostQL.Core.Model;
using BifrostQL.MySql;
using FluentAssertions;
using MySqlConnector;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

[Collection("MySqlFullIntegration")]
public class MySqlFullIntegrationTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_MYSQL environment variable not set");
            return;
        }

        _testDbName = $"bifrost_full_int_{Guid.NewGuid():N}";

        await using var masterConn = new MySqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new MySqlCommand($"CREATE DATABASE `{_testDbName}`", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new MySqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
        _connectionString = builder.ConnectionString;

        var factory = new MySqlDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();

        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
            if (masterConnString == null) return;

            await using var conn = new MySqlConnection(masterConnString);
            await conn.OpenAsync();
            var dropCmd = new MySqlCommand($"DROP DATABASE IF EXISTS `{_testDbName}`", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "CREATE TABLE Categories (CategoryId INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(50) NOT NULL)",
            "CREATE TABLE Products (ProductId INT AUTO_INCREMENT PRIMARY KEY, CategoryId INT NOT NULL, Name VARCHAR(100) NOT NULL, Price DECIMAL(10,2) NOT NULL, CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryId) REFERENCES Categories(CategoryId))",
            "CREATE TABLE Customers (CustomerId INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(100) NOT NULL, Email VARCHAR(100) NOT NULL)",
            "CREATE TABLE Orders (OrderId INT AUTO_INCREMENT PRIMARY KEY, CustomerId INT NOT NULL, OrderDate DATETIME NOT NULL, TotalAmount DECIMAL(10,2) NOT NULL, CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES Customers(CustomerId))"
        };

        foreach (var statement in statements)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var statements = new[]
        {
            "INSERT INTO Categories (Name) VALUES ('Electronics'), ('Books')",
            "INSERT INTO Products (CategoryId, Name, Price) VALUES (1, 'Laptop', 999.99), (1, 'Mouse', 29.99)",
            "INSERT INTO Customers (Name, Email) VALUES ('John Doe', 'john@example.com')",
            "INSERT INTO Orders (CustomerId, OrderDate, TotalAmount) VALUES (1, '2024-01-15', 1029.98)"
        };

        foreach (var statement in statements)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = "query { products { productId name price } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_ProductWithCategory_ShouldReturnJoinedData()
    {
        var query = "query { products(filter: { productId: { _eq: 1 } }) { name category { name } } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Mutation_InsertProduct_ShouldCreateRecord()
    {
        var mutation = @"mutation { products(insert: { categoryId: 1, name: ""New Product"", price: 99.99 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();
    }
}
