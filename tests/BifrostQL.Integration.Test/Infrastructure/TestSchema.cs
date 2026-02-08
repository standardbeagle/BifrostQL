using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// Provides DDL and seed data for the integration test database.
/// Schema: Categories, Products, Customers, Orders, OrderItems.
/// </summary>
public static class TestSchema
{
    public static string GetCreateTablesSql(ISqlDialect dialect)
    {
        if (dialect is BifrostQL.Sqlite.SqliteDialect)
            return SqliteCreateTables;
        if (dialect is SqlServerDialect)
            return SqlServerCreateTables;
        if (dialect is BifrostQL.Ngsql.PostgresDialect)
            return PostgresCreateTables;
        if (dialect is BifrostQL.MySql.MySqlDialect)
            return MySqlCreateTables;

        throw new NotSupportedException($"Unknown dialect: {dialect.GetType().Name}");
    }

    private const string SqliteCreateTables = """
        CREATE TABLE IF NOT EXISTS Categories (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Description TEXT
        );

        CREATE TABLE IF NOT EXISTS Products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CategoryId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Price REAL NOT NULL,
            Stock INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
        );

        CREATE TABLE IF NOT EXISTS Customers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Email TEXT NOT NULL,
            City TEXT
        );

        CREATE TABLE IF NOT EXISTS Orders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CustomerId INTEGER NOT NULL,
            OrderDate TEXT NOT NULL,
            Total REAL NOT NULL,
            Status TEXT NOT NULL DEFAULT 'Pending',
            FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
        );

        CREATE TABLE IF NOT EXISTS OrderItems (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderId INTEGER NOT NULL,
            ProductId INTEGER NOT NULL,
            Quantity INTEGER NOT NULL,
            UnitPrice REAL NOT NULL,
            FOREIGN KEY (OrderId) REFERENCES Orders(Id),
            FOREIGN KEY (ProductId) REFERENCES Products(Id)
        );
        """;

    private const string SqlServerCreateTables = """
        IF OBJECT_ID('OrderItems', 'U') IS NOT NULL DROP TABLE OrderItems;
        IF OBJECT_ID('Orders', 'U') IS NOT NULL DROP TABLE Orders;
        IF OBJECT_ID('Customers', 'U') IS NOT NULL DROP TABLE Customers;
        IF OBJECT_ID('Products', 'U') IS NOT NULL DROP TABLE Products;
        IF OBJECT_ID('Categories', 'U') IS NOT NULL DROP TABLE Categories;

        CREATE TABLE Categories (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Name NVARCHAR(100) NOT NULL,
            Description NVARCHAR(500) NULL
        );

        CREATE TABLE Products (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            CategoryId INT NOT NULL,
            Name NVARCHAR(200) NOT NULL,
            Price DECIMAL(18,2) NOT NULL,
            Stock INT NOT NULL DEFAULT 0,
            FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
        );

        CREATE TABLE Customers (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Name NVARCHAR(100) NOT NULL,
            Email NVARCHAR(200) NOT NULL,
            City NVARCHAR(100) NULL
        );

        CREATE TABLE Orders (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            CustomerId INT NOT NULL,
            OrderDate DATETIME2 NOT NULL,
            Total DECIMAL(18,2) NOT NULL,
            Status NVARCHAR(50) NOT NULL DEFAULT 'Pending',
            FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
        );

        CREATE TABLE OrderItems (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            OrderId INT NOT NULL,
            ProductId INT NOT NULL,
            Quantity INT NOT NULL,
            UnitPrice DECIMAL(18,2) NOT NULL,
            FOREIGN KEY (OrderId) REFERENCES Orders(Id),
            FOREIGN KEY (ProductId) REFERENCES Products(Id)
        );
        """;

    private const string PostgresCreateTables = """
        DROP TABLE IF EXISTS "OrderItems" CASCADE;
        DROP TABLE IF EXISTS "Orders" CASCADE;
        DROP TABLE IF EXISTS "Customers" CASCADE;
        DROP TABLE IF EXISTS "Products" CASCADE;
        DROP TABLE IF EXISTS "Categories" CASCADE;

        CREATE TABLE "Categories" (
            "Id" SERIAL PRIMARY KEY,
            "Name" VARCHAR(100) NOT NULL,
            "Description" VARCHAR(500)
        );

        CREATE TABLE "Products" (
            "Id" SERIAL PRIMARY KEY,
            "CategoryId" INTEGER NOT NULL REFERENCES "Categories"("Id"),
            "Name" VARCHAR(200) NOT NULL,
            "Price" DECIMAL(18,2) NOT NULL,
            "Stock" INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE "Customers" (
            "Id" SERIAL PRIMARY KEY,
            "Name" VARCHAR(100) NOT NULL,
            "Email" VARCHAR(200) NOT NULL,
            "City" VARCHAR(100)
        );

        CREATE TABLE "Orders" (
            "Id" SERIAL PRIMARY KEY,
            "CustomerId" INTEGER NOT NULL REFERENCES "Customers"("Id"),
            "OrderDate" TIMESTAMP NOT NULL,
            "Total" DECIMAL(18,2) NOT NULL,
            "Status" VARCHAR(50) NOT NULL DEFAULT 'Pending'
        );

        CREATE TABLE "OrderItems" (
            "Id" SERIAL PRIMARY KEY,
            "OrderId" INTEGER NOT NULL REFERENCES "Orders"("Id"),
            "ProductId" INTEGER NOT NULL REFERENCES "Products"("Id"),
            "Quantity" INTEGER NOT NULL,
            "UnitPrice" DECIMAL(18,2) NOT NULL
        );
        """;

    private const string MySqlCreateTables = """
        DROP TABLE IF EXISTS `OrderItems`;
        DROP TABLE IF EXISTS `Orders`;
        DROP TABLE IF EXISTS `Customers`;
        DROP TABLE IF EXISTS `Products`;
        DROP TABLE IF EXISTS `Categories`;

        CREATE TABLE `Categories` (
            `Id` INT AUTO_INCREMENT PRIMARY KEY,
            `Name` VARCHAR(100) NOT NULL,
            `Description` VARCHAR(500)
        );

        CREATE TABLE `Products` (
            `Id` INT AUTO_INCREMENT PRIMARY KEY,
            `CategoryId` INT NOT NULL,
            `Name` VARCHAR(200) NOT NULL,
            `Price` DECIMAL(18,2) NOT NULL,
            `Stock` INT NOT NULL DEFAULT 0,
            FOREIGN KEY (`CategoryId`) REFERENCES `Categories`(`Id`)
        );

        CREATE TABLE `Customers` (
            `Id` INT AUTO_INCREMENT PRIMARY KEY,
            `Name` VARCHAR(100) NOT NULL,
            `Email` VARCHAR(200) NOT NULL,
            `City` VARCHAR(100)
        );

        CREATE TABLE `Orders` (
            `Id` INT AUTO_INCREMENT PRIMARY KEY,
            `CustomerId` INT NOT NULL,
            `OrderDate` DATETIME NOT NULL,
            `Total` DECIMAL(18,2) NOT NULL,
            `Status` VARCHAR(50) NOT NULL DEFAULT 'Pending',
            FOREIGN KEY (`CustomerId`) REFERENCES `Customers`(`Id`)
        );

        CREATE TABLE `OrderItems` (
            `Id` INT AUTO_INCREMENT PRIMARY KEY,
            `OrderId` INT NOT NULL,
            `ProductId` INT NOT NULL,
            `Quantity` INT NOT NULL,
            `UnitPrice` DECIMAL(18,2) NOT NULL,
            FOREIGN KEY (`OrderId`) REFERENCES `Orders`(`Id`),
            FOREIGN KEY (`ProductId`) REFERENCES `Products`(`Id`)
        );
        """;

    /// <summary>
    /// Seeds deterministic test data. Must be called after tables are created.
    /// </summary>
    public static async Task SeedDataAsync(DbConnection conn, ISqlDialect dialect)
    {
        var prefix = dialect.ParameterPrefix;

        // 5 categories
        var categories = new[]
        {
            ("Electronics", "Electronic devices and accessories"),
            ("Books", "Physical and digital books"),
            ("Clothing", "Apparel and fashion items"),
            ("Home & Garden", "Home improvement and garden supplies"),
            ("Sports", "Sporting goods and equipment"),
        };

        for (var i = 0; i < categories.Length; i++)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {dialect.EscapeIdentifier("Categories")} ({dialect.EscapeIdentifier("Name")}, {dialect.EscapeIdentifier("Description")}) VALUES ({prefix}name, {prefix}desc)";
            AddParam(cmd, $"{prefix}name", categories[i].Item1);
            AddParam(cmd, $"{prefix}desc", categories[i].Item2);
            await cmd.ExecuteNonQueryAsync();
        }

        // 20 products (4 per category)
        var products = new (int catId, string name, decimal price, int stock)[]
        {
            (1, "Laptop", 999.99m, 50), (1, "Phone", 699.99m, 100), (1, "Tablet", 449.99m, 75), (1, "Headphones", 149.99m, 200),
            (2, "C# in Depth", 44.99m, 30), (2, "Clean Code", 39.99m, 45), (2, "Design Patterns", 49.99m, 25), (2, "The Pragmatic Programmer", 42.99m, 35),
            (3, "T-Shirt", 19.99m, 500), (3, "Jeans", 59.99m, 200), (3, "Jacket", 89.99m, 100), (3, "Sneakers", 79.99m, 150),
            (4, "Garden Hose", 29.99m, 80), (4, "Plant Pot", 12.99m, 300), (4, "Lawn Mower", 299.99m, 20), (4, "Fertilizer", 15.99m, 150),
            (5, "Basketball", 24.99m, 100), (5, "Tennis Racket", 89.99m, 60), (5, "Yoga Mat", 34.99m, 200), (5, "Dumbbells", 49.99m, 80),
        };

        for (var i = 0; i < products.Length; i++)
        {
            var p = products[i];
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {dialect.EscapeIdentifier("Products")} ({dialect.EscapeIdentifier("CategoryId")}, {dialect.EscapeIdentifier("Name")}, {dialect.EscapeIdentifier("Price")}, {dialect.EscapeIdentifier("Stock")}) VALUES ({prefix}catId, {prefix}name, {prefix}price, {prefix}stock)";
            AddParam(cmd, $"{prefix}catId", p.catId);
            AddParam(cmd, $"{prefix}name", p.name);
            AddParam(cmd, $"{prefix}price", p.price);
            AddParam(cmd, $"{prefix}stock", p.stock);
            await cmd.ExecuteNonQueryAsync();
        }

        // 10 customers
        var customers = new (string name, string email, string city)[]
        {
            ("Alice Johnson", "alice@example.com", "New York"),
            ("Bob Smith", "bob@example.com", "Los Angeles"),
            ("Charlie Brown", "charlie@example.com", "Chicago"),
            ("Diana Prince", "diana@example.com", "Houston"),
            ("Eve Adams", "eve@example.com", "Phoenix"),
            ("Frank Castle", "frank@example.com", "Philadelphia"),
            ("Grace Hopper", "grace@example.com", "San Antonio"),
            ("Henry Ford", "henry@example.com", "San Diego"),
            ("Ivy Chen", "ivy@example.com", "Dallas"),
            ("Jack Ryan", "jack@example.com", "San Jose"),
        };

        for (var i = 0; i < customers.Length; i++)
        {
            var c = customers[i];
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {dialect.EscapeIdentifier("Customers")} ({dialect.EscapeIdentifier("Name")}, {dialect.EscapeIdentifier("Email")}, {dialect.EscapeIdentifier("City")}) VALUES ({prefix}name, {prefix}email, {prefix}city)";
            AddParam(cmd, $"{prefix}name", c.name);
            AddParam(cmd, $"{prefix}email", c.email);
            AddParam(cmd, $"{prefix}city", c.city);
            await cmd.ExecuteNonQueryAsync();
        }

        // 30 orders (3 per customer)
        var statuses = new[] { "Pending", "Shipped", "Delivered" };
        var baseDate = new DateTime(2024, 1, 1);
        for (var custId = 1; custId <= 10; custId++)
        {
            for (var j = 0; j < 3; j++)
            {
                var orderDate = baseDate.AddDays((custId - 1) * 10 + j * 3);
                var total = 100m + custId * 50m + j * 25m;
                var status = statuses[j % 3];

                var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO {dialect.EscapeIdentifier("Orders")} ({dialect.EscapeIdentifier("CustomerId")}, {dialect.EscapeIdentifier("OrderDate")}, {dialect.EscapeIdentifier("Total")}, {dialect.EscapeIdentifier("Status")}) VALUES ({prefix}custId, {prefix}orderDate, {prefix}total, {prefix}status)";
                AddParam(cmd, $"{prefix}custId", custId);
                AddParam(cmd, $"{prefix}orderDate", orderDate);
                AddParam(cmd, $"{prefix}total", total);
                AddParam(cmd, $"{prefix}status", status);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // 60 order items (2 per order)
        for (var orderId = 1; orderId <= 30; orderId++)
        {
            for (var j = 0; j < 2; j++)
            {
                var productId = ((orderId + j - 1) % 20) + 1;
                var quantity = (orderId % 5) + 1;
                var unitPrice = products[productId - 1].price;

                var cmd = conn.CreateCommand();
                cmd.CommandText = $"INSERT INTO {dialect.EscapeIdentifier("OrderItems")} ({dialect.EscapeIdentifier("OrderId")}, {dialect.EscapeIdentifier("ProductId")}, {dialect.EscapeIdentifier("Quantity")}, {dialect.EscapeIdentifier("UnitPrice")}) VALUES ({prefix}orderId, {prefix}productId, {prefix}qty, {prefix}unitPrice)";
                AddParam(cmd, $"{prefix}orderId", orderId);
                AddParam(cmd, $"{prefix}productId", productId);
                AddParam(cmd, $"{prefix}qty", quantity);
                AddParam(cmd, $"{prefix}unitPrice", unitPrice);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Builds the IDbModel matching the test schema using DbModelTestFixture conventions.
    /// </summary>
    public static IDbModel BuildDbModel()
    {
        return new TestSchemaDbModel();
    }

    private sealed class TestSchemaDbModel : IDbModel
    {
        private readonly Dictionary<string, IDbTable> _tablesByDbName;
        private readonly Dictionary<string, IDbTable> _tablesByGraphQlName;

        public TestSchemaDbModel()
        {
            var categories = BuildTable("Categories", "", new[]
            {
                Col("Id", "int", isPrimaryKey: true),
                Col("Name", "nvarchar"),
                Col("Description", "nvarchar", isNullable: true),
            });
            var products = BuildTable("Products", "", new[]
            {
                Col("Id", "int", isPrimaryKey: true),
                Col("CategoryId", "int"),
                Col("Name", "nvarchar"),
                Col("Price", "decimal"),
                Col("Stock", "int"),
            });
            var customers = BuildTable("Customers", "", new[]
            {
                Col("Id", "int", isPrimaryKey: true),
                Col("Name", "nvarchar"),
                Col("Email", "nvarchar"),
                Col("City", "nvarchar", isNullable: true),
            });
            var orders = BuildTable("Orders", "", new[]
            {
                Col("Id", "int", isPrimaryKey: true),
                Col("CustomerId", "int"),
                Col("OrderDate", "datetime2"),
                Col("Total", "decimal"),
                Col("Status", "nvarchar"),
            });
            var orderItems = BuildTable("OrderItems", "", new[]
            {
                Col("Id", "int", isPrimaryKey: true),
                Col("OrderId", "int"),
                Col("ProductId", "int"),
                Col("Quantity", "int"),
                Col("UnitPrice", "decimal"),
            });

            // SingleLinks (ManyToOne: child -> parent)
            products.SingleLinks["category"] = new TableLinkDto
            {
                Name = "Products->Categories",
                ChildTable = products,
                ChildId = products.ColumnLookup["CategoryId"],
                ParentTable = categories,
                ParentId = categories.ColumnLookup["Id"],
            };
            orderItems.SingleLinks["order"] = new TableLinkDto
            {
                Name = "OrderItems->Orders",
                ChildTable = orderItems,
                ChildId = orderItems.ColumnLookup["OrderId"],
                ParentTable = orders,
                ParentId = orders.ColumnLookup["Id"],
            };
            orderItems.SingleLinks["product"] = new TableLinkDto
            {
                Name = "OrderItems->Products",
                ChildTable = orderItems,
                ChildId = orderItems.ColumnLookup["ProductId"],
                ParentTable = products,
                ParentId = products.ColumnLookup["Id"],
            };
            orders.SingleLinks["customer"] = new TableLinkDto
            {
                Name = "Orders->Customers",
                ChildTable = orders,
                ChildId = orders.ColumnLookup["CustomerId"],
                ParentTable = customers,
                ParentId = customers.ColumnLookup["Id"],
            };

            // MultiLinks (OneToMany: parent -> children)
            categories.MultiLinks["products"] = new TableLinkDto
            {
                Name = "Categories->Products",
                ParentTable = categories,
                ParentId = categories.ColumnLookup["Id"],
                ChildTable = products,
                ChildId = products.ColumnLookup["CategoryId"],
            };
            orders.MultiLinks["items"] = new TableLinkDto
            {
                Name = "Orders->OrderItems",
                ParentTable = orders,
                ParentId = orders.ColumnLookup["Id"],
                ChildTable = orderItems,
                ChildId = orderItems.ColumnLookup["OrderId"],
            };
            customers.MultiLinks["orders"] = new TableLinkDto
            {
                Name = "Customers->Orders",
                ParentTable = customers,
                ParentId = customers.ColumnLookup["Id"],
                ChildTable = orders,
                ChildId = orders.ColumnLookup["CustomerId"],
            };

            var allTables = new IDbTable[] { categories, products, customers, orders, orderItems };
            Tables = allTables;
            _tablesByDbName = allTables.ToDictionary(t => t.DbName, t => t);
            _tablesByGraphQlName = allTables.ToDictionary(t => t.GraphQlName, t => t);
        }

        public IReadOnlyCollection<IDbTable> Tables { get; }
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

        public string? GetMetadataValue(string property) =>
            Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;

        public bool GetMetadataBool(string property, bool defaultValue) =>
            Metadata.TryGetValue(property, out var v) ? v?.ToString() == "true" : defaultValue;

        public IDbTable GetTableByFullGraphQlName(string fullName) =>
            _tablesByGraphQlName.TryGetValue(fullName, out var t) ? t : throw new KeyNotFoundException($"Table '{fullName}' not found");

        public IDbTable GetTableFromDbName(string tableName) =>
            _tablesByDbName.TryGetValue(tableName, out var t) ? t : throw new KeyNotFoundException($"Table '{tableName}' not found");

        private static DbTable BuildTable(string name, string schema, ColumnDto[] columns)
        {
            var colLookup = columns.ToDictionary(c => c.ColumnName, c => c);
            var gqlLookup = columns.ToDictionary(c => c.GraphQlName, c => c);
            return new DbTable
            {
                DbName = name,
                GraphQlName = name,
                NormalizedName = name,
                TableSchema = schema,
                TableType = "BASE TABLE",
                ColumnLookup = colLookup,
                GraphQlLookup = gqlLookup,
            };
        }

        private static ColumnDto Col(string name, string dataType, bool isPrimaryKey = false, bool isNullable = false)
        {
            return new ColumnDto
            {
                ColumnName = name,
                GraphQlName = name,
                NormalizedName = name.ToLowerInvariant(),
                DataType = dataType,
                IsPrimaryKey = isPrimaryKey,
                IsNullable = isNullable,
            };
        }
    }

    /// <summary>
    /// Known data counts for assertions.
    /// </summary>
    public static class Counts
    {
        public const int Categories = 5;
        public const int Products = 20;
        public const int Customers = 10;
        public const int Orders = 30;
        public const int OrderItems = 60;
        public const int ProductsPerCategory = 4;
        public const int OrdersPerCustomer = 3;
        public const int ItemsPerOrder = 2;
    }
}
