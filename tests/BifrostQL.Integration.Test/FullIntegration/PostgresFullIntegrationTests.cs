using BifrostQL.Core.Model;
using BifrostQL.Ngsql;
using FluentAssertions;
using Npgsql;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Full integration tests for PostgreSQL with dynamically loaded schema.
/// Only runs when BIFROST_TEST_POSTGRES env var is set.
/// </summary>
[Collection("PostgresFullIntegration")]
public class PostgresFullIntegrationTests : FullIntegrationTestBase, IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_POSTGRES environment variable not set");
            return;
        }

        _testDbName = $"bifrost_full_int_{Guid.NewGuid():N}";

        await using var masterConn = new NpgsqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new NpgsqlCommand($"CREATE DATABASE {_testDbName}", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(masterConnString) { Database = _testDbName };
        _connectionString = builder.ConnectionString;

        var factory = new PostgresDbConnFactory(_connectionString);
        await base.InitializeAsync(factory, CreateSchemaAsync, SeedDataAsync);
    }

    public async Task DisposeAsync()
    {
        await base.CleanupAsync();

        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
            if (masterConnString == null) return;

            await using var conn = new NpgsqlConnection(masterConnString);
            await conn.OpenAsync();

            var terminateCmd = new NpgsqlCommand($@"
                SELECT pg_terminate_backend(pg_stat_activity.pid)
                FROM pg_stat_activity
                WHERE pg_stat_activity.datname = '{_testDbName}'
                AND pid <> pg_backend_pid()", conn);
            await terminateCmd.ExecuteNonQueryAsync();

            var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_testDbName}", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static async Task CreateSchemaAsync(System.Data.Common.DbConnection conn)
    {
        var ddl = @"
CREATE TABLE categories (
    category_id SERIAL PRIMARY KEY,
    name VARCHAR(50) NOT NULL,
    description VARCHAR(200) NULL
);

CREATE TABLE products (
    product_id SERIAL PRIMARY KEY,
    category_id INTEGER NOT NULL,
    name VARCHAR(100) NOT NULL,
    price DECIMAL(10,2) NOT NULL,
    stock INTEGER NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT fk_products_categories FOREIGN KEY (category_id) REFERENCES categories(category_id)
);

CREATE TABLE customers (
    customer_id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(100) NOT NULL,
    city VARCHAR(50) NULL,
    country VARCHAR(50) NOT NULL
);

CREATE TABLE orders (
    order_id SERIAL PRIMARY KEY,
    customer_id INTEGER NOT NULL,
    order_date TIMESTAMP NOT NULL,
    total_amount DECIMAL(10,2) NOT NULL,
    status VARCHAR(20) NOT NULL,
    CONSTRAINT fk_orders_customers FOREIGN KEY (customer_id) REFERENCES customers(customer_id)
);

CREATE TABLE order_items (
    order_item_id SERIAL PRIMARY KEY,
    order_id INTEGER NOT NULL,
    product_id INTEGER NOT NULL,
    quantity INTEGER NOT NULL,
    unit_price DECIMAL(10,2) NOT NULL,
    CONSTRAINT fk_order_items_orders FOREIGN KEY (order_id) REFERENCES orders(order_id),
    CONSTRAINT fk_order_items_products FOREIGN KEY (product_id) REFERENCES products(product_id)
);
";

        var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedDataAsync(System.Data.Common.DbConnection conn)
    {
        var seed = @"
INSERT INTO categories (name, description) VALUES ('Electronics', 'Electronic devices');
INSERT INTO categories (name, description) VALUES ('Books', 'Physical and digital books');

INSERT INTO products (category_id, name, price, stock, is_active) VALUES (1, 'Laptop', 999.99, 10, true);
INSERT INTO products (category_id, name, price, stock, is_active) VALUES (1, 'Mouse', 29.99, 50, true);
INSERT INTO products (category_id, name, price, stock, is_active) VALUES (2, 'Book', 49.99, 20, true);

INSERT INTO customers (name, email, city, country) VALUES ('John Doe', 'john@example.com', 'New York', 'USA');
INSERT INTO customers (name, email, city, country) VALUES ('Jane Smith', 'jane@example.com', 'London', 'UK');

INSERT INTO orders (customer_id, order_date, total_amount, status) VALUES (1, '2024-01-15', 1029.98, 'Delivered');
INSERT INTO orders (customer_id, order_date, total_amount, status) VALUES (2, '2024-02-10', 49.99, 'Shipped');

INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES (1, 1, 1, 999.99);
INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES (1, 2, 1, 29.99);
INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES (2, 3, 1, 49.99);
";

        var cmd = conn.CreateCommand();
        cmd.CommandText = seed;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllProducts()
    {
        var query = "query { products { productId name price } }";
        var result = await ExecuteQueryAsync(query);

        result.Errors.Should().BeNullOrEmpty();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
            JsonSerializer.Serialize(((Dictionary<string, object?>)result.Data!)["products"]));

        products.Should().HaveCount(3);
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
        var mutation = @"mutation { products(insert: { categoryId: 1, name: ""New"", price: 99.99, stock: 5 }) }";
        var result = await ExecuteQueryAsync(mutation);

        result.Errors.Should().BeNullOrEmpty();
    }
}
