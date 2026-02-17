using BifrostQL.Core.Model;
using BifrostQL.MySql;
using FluentAssertions;
using GraphQL;
using GraphQL.Execution;
using MySqlConnector;
using System.Text.Json;

namespace BifrostQL.Integration.Test.FullIntegration;

/// <summary>
/// Full integration tests for MySQL with dynamically loaded schema.
/// Only runs when BIFROST_TEST_MYSQL env var is set.
/// Covers: schema reading via information_schema, queries with backtick escaping,
/// LIMIT/OFFSET pagination, CONCAT-based LIKE patterns, AUTO_INCREMENT identity,
/// multi-table joins, and all mutation types (INSERT/UPDATE/DELETE).
/// </summary>
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
        // MySQL requires separate statements (no multi-statement DDL with FKs in single batch).
        // Use lowercase names: MySQL on Linux is case-sensitive by default.
        var statements = new[]
        {
            @"CREATE TABLE categories (
                categoryid INT AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(50) NOT NULL,
                description VARCHAR(200) NULL
            )",
            @"CREATE TABLE products (
                productid INT AUTO_INCREMENT PRIMARY KEY,
                categoryid INT NOT NULL,
                name VARCHAR(100) NOT NULL,
                price DECIMAL(10,2) NOT NULL,
                stock INT NOT NULL DEFAULT 0,
                isactive BOOLEAN NOT NULL DEFAULT TRUE,
                CONSTRAINT fk_products_categories FOREIGN KEY (categoryid) REFERENCES categories(categoryid)
            )",
            @"CREATE TABLE customers (
                customerid INT AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(100) NOT NULL,
                email VARCHAR(100) NOT NULL,
                city VARCHAR(50) NULL,
                country VARCHAR(50) NOT NULL
            )",
            @"CREATE TABLE orders (
                orderid INT AUTO_INCREMENT PRIMARY KEY,
                customerid INT NOT NULL,
                orderdate DATETIME NOT NULL,
                totalamount DECIMAL(10,2) NOT NULL,
                status VARCHAR(20) NOT NULL,
                CONSTRAINT fk_orders_customers FOREIGN KEY (customerid) REFERENCES customers(customerid)
            )",
            @"CREATE TABLE orderitems (
                orderitemid INT AUTO_INCREMENT PRIMARY KEY,
                orderid INT NOT NULL,
                productid INT NOT NULL,
                quantity INT NOT NULL,
                unitprice DECIMAL(10,2) NOT NULL,
                CONSTRAINT fk_order_items_orders FOREIGN KEY (orderid) REFERENCES orders(orderid),
                CONSTRAINT fk_order_items_products FOREIGN KEY (productid) REFERENCES products(productid)
            )"
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
            "INSERT INTO categories (name, description) VALUES ('Electronics', 'Electronic devices'), ('Books', 'Physical and digital books'), ('Sports', 'Sporting goods')",

            "INSERT INTO products (categoryid, name, price, stock, isactive) VALUES (1, 'Laptop', 999.99, 10, true), (1, 'Mouse', 29.99, 50, true), (2, 'Clean Code', 49.99, 20, true), (2, 'Design Patterns', 44.99, 15, true), (3, 'Basketball', 24.99, 100, true)",

            "INSERT INTO customers (name, email, city, country) VALUES ('John Doe', 'john@example.com', 'New York', 'USA'), ('Jane Smith', 'jane@example.com', 'London', 'UK'), ('Bob Wilson', 'bob@example.com', NULL, 'Canada')",

            "INSERT INTO orders (customerid, orderdate, totalamount, status) VALUES (1, '2024-01-15', 1029.98, 'Delivered'), (2, '2024-02-10', 49.99, 'Shipped'), (1, '2024-03-05', 29.99, 'Pending')",

            "INSERT INTO orderitems (orderid, productid, quantity, unitprice) VALUES (1, 1, 1, 999.99), (1, 2, 1, 29.99), (2, 3, 1, 49.99), (3, 2, 1, 29.99)"
        };

        foreach (var statement in statements)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Extracts the data list from a paged query result.
    /// BifrostQL wraps top-level queries in _paged types with data/total/offset/limit.
    /// result.Data is a RootExecutionNode; ToValue() converts to Dictionary hierarchy.
    /// </summary>
    private static List<Dictionary<string, JsonElement>> ExtractPagedData(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        root.Should().ContainKey(tableName);
        var json = JsonSerializer.Serialize(root[tableName]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        paged.Should().ContainKey("data");
        var dataJson = paged["data"].GetRawText();
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(dataJson)!;
    }

    /// <summary>
    /// Asserts a query has no errors and returns the root dictionary.
    /// </summary>
    private static Dictionary<string, object?> AssertSuccess(ExecutionResult result)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        return (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null! : e.ToString();
    private static double Dbl(JsonElement e) => e.GetDouble();
    private static int Int(JsonElement e) => e.GetInt32();
    private static bool Bool(JsonElement e) => e.GetBoolean();

    // =============================================================
    // Schema Reading (information_schema)
    // =============================================================

    [Fact]
    public void Schema_ShouldLoadAllTables()
    {
        Model.Should().NotBeNull();
        var tableNames = Model.Tables.Select(t => t.DbName).ToList();
        tableNames.Should().Contain("categories");
        tableNames.Should().Contain("products");
        tableNames.Should().Contain("customers");
        tableNames.Should().Contain("orders");
        tableNames.Should().Contain("orderitems");
    }

    [Fact]
    public void Schema_ShouldDetectColumns()
    {
        var productsTable = Model.Tables.First(t => t.DbName == "products");
        var columnNames = productsTable.Columns.Select(c => c.ColumnName).ToList();
        columnNames.Should().Contain("productid");
        columnNames.Should().Contain("categoryid");
        columnNames.Should().Contain("name");
        columnNames.Should().Contain("price");
        columnNames.Should().Contain("stock");
        columnNames.Should().Contain("isactive");
    }

    [Fact]
    public void Schema_ShouldDetectPrimaryKeys()
    {
        var productsTable = Model.Tables.First(t => t.DbName == "products");
        var pkCol = productsTable.Columns.First(c => c.ColumnName == "productid");
        pkCol.IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public void Schema_AutoIncrementColumns_ShouldBeDetectedAsIdentity()
    {
        var productsTable = Model.Tables.First(t => t.DbName == "products");
        var idCol = productsTable.Columns.First(c => c.ColumnName == "productid");
        idCol.IsIdentity.Should().BeTrue("AUTO_INCREMENT columns should be detected as identity");

        var ordersTable = Model.Tables.First(t => t.DbName == "orders");
        ordersTable.Columns.First(c => c.ColumnName == "orderid").IsIdentity.Should().BeTrue();
    }

    [Fact]
    public void Schema_ShouldDetectForeignKeyLinks()
    {
        var productsTable = Model.Tables.First(t => t.DbName == "products");
        productsTable.SingleLinks.Should().NotBeEmpty("products has FK to categories");

        var ordersTable = Model.Tables.First(t => t.DbName == "orders");
        ordersTable.SingleLinks.Should().NotBeEmpty("orders has FK to customers");

        var orderitemsTable = Model.Tables.First(t => t.DbName == "orderitems");
        orderitemsTable.SingleLinks.Should().NotBeEmpty("orderitems has FK to orders and products");
    }

    [Fact]
    public void Schema_ShouldDetectNullableColumns()
    {
        var customersTable = Model.Tables.First(t => t.DbName == "customers");
        customersTable.Columns.First(c => c.ColumnName == "city").IsNullable.Should().BeTrue();
        customersTable.Columns.First(c => c.ColumnName == "name").IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Schema_TypeMapper_ShouldMapMySqlTypesCorrectly()
    {
        Model.TypeMapper.Should().BeOfType<MySqlTypeMapper>();
        var productsTable = Model.Tables.First(t => t.DbName == "products");
        var idCol = productsTable.Columns.First(c => c.ColumnName == "productid");
        Model.TypeMapper.GetGraphQlType(idCol.EffectiveDataType).Should().Be("Int");
    }

    // =============================================================
    // Queries - Basic SELECT (backtick escaping)
    // =============================================================

    [Fact]
    public async Task Query_AllProducts_ShouldReturnAllRows()
    {
        var query = "query { products { data { productid name price } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().HaveCount(5);
    }

    [Fact]
    public async Task Query_FilterByEquality_ShouldReturnMatchingRows()
    {
        var query = @"query { products(filter: { name: { _eq: ""Laptop"" } }) { data { productid name price } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Laptop");
    }

    [Fact]
    public async Task Query_FilterByIntEquality_ShouldWork()
    {
        var query = "query { products(filter: { productid: { _eq: 1 } }) { data { productid name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Int(products[0]["productid"]).Should().Be(1);
        Str(products[0]["name"]).Should().Be("Laptop");
    }

    [Fact]
    public async Task Query_FilterByLessThan_ShouldReturnCheapProducts()
    {
        var query = "query { products(filter: { price: { _lt: 50 } }) { data { name price } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().HaveCountGreaterThan(0);
        products.All(p => Dbl(p["price"]) < 50).Should().BeTrue();
    }

    [Fact]
    public async Task Query_FilterByGreaterThan_ShouldReturnExpensiveProducts()
    {
        var query = "query { products(filter: { price: { _gt: 100 } }) { data { name price } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Laptop");
    }

    [Fact]
    public async Task Query_FilterByIn_ShouldReturnMatchingStatuses()
    {
        var query = @"query { orders(filter: { status: { _in: [""Delivered"", ""Shipped""] } }) { data { orderid status } } }";
        var orders = ExtractPagedData(await ExecuteQueryAsync(query), "orders");
        orders.Should().HaveCount(2);
        orders.All(o =>
        {
            var status = Str(o["status"]);
            return status == "Delivered" || status == "Shipped";
        }).Should().BeTrue();
    }

    [Fact]
    public async Task Query_FilterByNullCity_ShouldReturnNullRows()
    {
        var query = "query { customers(filter: { city: { _eq: null } }) { data { name city } } }";
        var customers = ExtractPagedData(await ExecuteQueryAsync(query), "customers");
        customers.Should().ContainSingle();
        Str(customers[0]["name"]).Should().Be("Bob Wilson");
    }

    [Fact]
    public async Task Query_FilterByBoolean_ShouldWork()
    {
        var query = "query { products(filter: { isactive: { _eq: true } }) { data { name isactive } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().HaveCount(5);
        products.All(p => Bool(p["isactive"])).Should().BeTrue();
    }

    // =============================================================
    // LIKE Patterns with CONCAT()
    // =============================================================

    [Fact]
    public async Task Query_FilterByContains_LikeWithConcat()
    {
        var query = @"query { products(filter: { name: { _contains: ""top"" } }) { data { name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Laptop");
    }

    [Fact]
    public async Task Query_FilterByStartsWith_LikeWithConcat()
    {
        var query = @"query { products(filter: { name: { _starts_with: ""De"" } }) { data { name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Design Patterns");
    }

    [Fact]
    public async Task Query_FilterByEndsWith_LikeWithConcat()
    {
        var query = @"query { products(filter: { name: { _ends_with: ""Code"" } }) { data { name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Clean Code");
    }

    // =============================================================
    // Pagination - LIMIT/OFFSET
    // =============================================================

    [Fact]
    public async Task Query_WithLimit_ShouldReturnLimitedRows()
    {
        var query = "query { products(sort: [productid_asc], limit: 2) { data { productid name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_WithOffset_ShouldSkipRows()
    {
        var query = "query { products(sort: [productid_asc], offset: 2, limit: 2) { data { productid name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().HaveCount(2);
        Str(products[0]["name"]).Should().Be("Clean Code");
    }

    [Fact]
    public async Task Query_PageThrough_ShouldCoverAllRows()
    {
        var page1 = ExtractPagedData(await ExecuteQueryAsync(
            "query { products(sort: [productid_asc], offset: 0, limit: 3) { data { productid } } }"), "products");
        var page2 = ExtractPagedData(await ExecuteQueryAsync(
            "query { products(sort: [productid_asc], offset: 3, limit: 3) { data { productid } } }"), "products");

        page1.Should().HaveCount(3);
        page2.Should().HaveCount(2);

        var allIds = page1.Concat(page2).Select(p => Int(p["productid"])).ToList();
        allIds.Should().HaveCount(5);
        allIds.Distinct().Should().HaveCount(5);
    }

    [Fact]
    public async Task Query_OffsetBeyondData_ShouldReturnEmpty()
    {
        var query = "query { products(sort: [productid_asc], offset: 100, limit: 10) { data { productid } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().BeEmpty();
    }

    // =============================================================
    // Sorting
    // =============================================================

    [Fact]
    public async Task Query_SortByPriceAsc_ShouldReturnSorted()
    {
        var query = "query { products(sort: [price_asc]) { data { name price } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        var prices = products.Select(p => Dbl(p["price"])).ToList();
        prices.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Query_SortByNameDesc_ShouldReturnSorted()
    {
        var query = "query { products(sort: [name_desc]) { data { name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        var names = products.Select(p => Str(p["name"])).ToList();
        names.Should().BeInDescendingOrder();
    }

    // =============================================================
    // Joins Across Tables
    // =============================================================

    [Fact]
    public async Task Query_ProductWithCategory_ManyToOne()
    {
        var query = "query { products(filter: { productid: { _eq: 1 } }) { data { name categories { name description } } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();

        var catJson = products[0]["categories"].GetRawText();
        var category = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(catJson)!;
        Str(category["name"]).Should().Be("Electronics");
        Str(category["description"]).Should().Be("Electronic devices");
    }

    [Fact]
    public async Task Query_CategoryWithProducts_OneToMany()
    {
        var query = "query { categories(filter: { categoryid: { _eq: 1 } }) { data { name products { name price } } } }";
        var categories = ExtractPagedData(await ExecuteQueryAsync(query), "categories");
        categories.Should().ContainSingle();

        var prodsJson = categories[0]["products"].GetRawText();
        var products = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(prodsJson)!;
        products.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_OrderWithNestedJoins_ThreeLevels()
    {
        var query = @"query {
            orders(filter: { orderid: { _eq: 1 } }) {
                data {
                    orderdate
                    customers { name email }
                    orderitems {
                        quantity
                        products { name categories { name } }
                    }
                }
            }
        }";

        var orders = ExtractPagedData(await ExecuteQueryAsync(query), "orders");
        orders.Should().ContainSingle();

        var custJson = orders[0]["customers"].GetRawText();
        var customer = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(custJson)!;
        Str(customer["name"]).Should().Be("John Doe");

        var itemsJson = orders[0]["orderitems"].GetRawText();
        var items = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(itemsJson)!;
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Query_CustomerWithOrders_ShouldReturnMultipleOrders()
    {
        var query = "query { customers(filter: { customerid: { _eq: 1 } }) { data { name orders { status totalamount } } } }";
        var customers = ExtractPagedData(await ExecuteQueryAsync(query), "customers");
        customers.Should().ContainSingle();

        var ordersJson = customers[0]["orders"].GetRawText();
        var orders = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(ordersJson)!;
        orders.Should().HaveCount(2);
    }

    // =============================================================
    // Mutations - INSERT
    // =============================================================

    [Fact]
    public async Task Mutation_InsertProduct_ShouldCreateRecord()
    {
        var mutation = @"mutation { products(insert: { categoryid: 1, name: ""Keyboard"", price: 79.99, stock: 25, isactive: true }) }";
        var result = await ExecuteQueryAsync(mutation);
        result.Errors.Should().BeNullOrEmpty();

        var verifyQuery = @"query { products(filter: { name: { _eq: ""Keyboard"" } }) { data { productid name price stock } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(verifyQuery), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Keyboard");
        Dbl(products[0]["price"]).Should().BeApproximately(79.99, 0.01);
    }

    [Fact]
    public async Task Mutation_InsertProduct_AutoIncrement_ShouldAutoIncrement()
    {
        var mutation1 = @"mutation { products(insert: { categoryid: 2, name: ""Book A"", price: 19.99, stock: 10, isactive: true }) }";
        var mutation2 = @"mutation { products(insert: { categoryid: 2, name: ""Book B"", price: 29.99, stock: 5, isactive: true }) }";

        var result1 = await ExecuteQueryAsync(mutation1);
        result1.Errors.Should().BeNullOrEmpty();

        var result2 = await ExecuteQueryAsync(mutation2);
        result2.Errors.Should().BeNullOrEmpty();

        var verifyQuery = @"query { products(filter: { name: { _starts_with: ""Book "" } }, sort: [productid_asc]) { data { productid name } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(verifyQuery), "products");
        products.Should().HaveCount(2);

        var id1 = Int(products[0]["productid"]);
        var id2 = Int(products[1]["productid"]);
        id2.Should().BeGreaterThan(id1);
    }

    // =============================================================
    // Mutations - UPDATE
    // =============================================================

    [Fact]
    public async Task Mutation_UpdateProduct_ShouldModifyRecord()
    {
        var mutation = @"mutation { products(update: { productid: 1, categoryid: 1, name: ""Laptop"", price: 899.99, stock: 15, isactive: true }) }";
        var result = await ExecuteQueryAsync(mutation);
        result.Errors.Should().BeNullOrEmpty();

        var verifyQuery = "query { products(filter: { productid: { _eq: 1 } }) { data { price stock } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(verifyQuery), "products");

        Dbl(products[0]["price"]).Should().BeApproximately(899.99, 0.01);
        Int(products[0]["stock"]).Should().Be(15);
    }

    // =============================================================
    // Mutations - DELETE
    // =============================================================

    [Fact]
    public async Task Mutation_DeleteProduct_ShouldRemoveRecord()
    {
        var mutation = "mutation { products(delete: { productid: 5 }) }";
        var result = await ExecuteQueryAsync(mutation);
        result.Errors.Should().BeNullOrEmpty();

        var verifyQuery = "query { products(filter: { productid: { _eq: 5 } }) { data { productid } } }";
        var products = ExtractPagedData(await ExecuteQueryAsync(verifyQuery), "products");
        products.Should().BeEmpty();
    }

    // =============================================================
    // Combined: Filter + Sort + Paginate
    // =============================================================

    [Fact]
    public async Task Query_FilterSortPaginate_Combined()
    {
        var query = @"query { products(
            filter: { categoryid: { _eq: 1 } },
            sort: [price_desc],
            limit: 1,
            offset: 0
        ) { data { name price } } }";

        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Laptop");
    }

    [Fact]
    public async Task Query_FilterSortPaginate_SecondPage()
    {
        var query = @"query { products(
            filter: { categoryid: { _eq: 1 } },
            sort: [price_desc],
            limit: 1,
            offset: 1
        ) { data { name price } } }";

        var products = ExtractPagedData(await ExecuteQueryAsync(query), "products");
        products.Should().ContainSingle();
        Str(products[0]["name"]).Should().Be("Mouse");
    }
}
