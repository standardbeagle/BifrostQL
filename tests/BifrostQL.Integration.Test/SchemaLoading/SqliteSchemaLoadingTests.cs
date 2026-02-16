using BifrostQL.Core.Model;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// Tests that DbModelLoader correctly reads SQLite schemas into DbModel.
/// SQLite tests always run (in-memory database).
/// </summary>
[Collection("SqliteSchemaLoading")]
public class SqliteSchemaLoadingTests : IAsyncLifetime
{
    private string? _connectionString;
    private SqliteConnection? _keepAliveConnection;
    private IDbModel? _loadedModel;

    public async Task InitializeAsync()
    {
        // Use shared cache in-memory database so multiple connections access the same DB
        _connectionString = "Data Source=bifrost_test;Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        await _keepAliveConnection.OpenAsync();

        // Create complex schema
        await CreateComplexSchemaAsync(_keepAliveConnection);

        // Load schema using DbModelLoader
        var factory = new SqliteDbConnFactory(_connectionString);
        var metadataLoader = new MetadataLoader(Array.Empty<string>());
        var loader = new DbModelLoader(factory, metadataLoader);
        _loadedModel = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAliveConnection != null)
        {
            await _keepAliveConnection.DisposeAsync();
        }
    }

    private static async Task CreateComplexSchemaAsync(SqliteConnection conn)
    {
        var statements = new[]
        {
            @"CREATE TABLE DataTypes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StringCol TEXT NOT NULL,
                IntCol INTEGER NOT NULL,
                BigIntCol INTEGER NULL,
                DecimalCol REAL NOT NULL,
                FloatCol REAL NULL,
                BoolCol INTEGER NOT NULL,
                DateCol TEXT NULL,
                DateTimeCol TEXT NOT NULL,
                TimeCol TEXT NULL,
                BlobCol BLOB NULL
            )",

            @"CREATE TABLE CompositePK (
                TenantId INTEGER NOT NULL,
                UserId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                PRIMARY KEY (TenantId, UserId)
            )",

            @"CREATE TABLE Orders (
                OrderId INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomerId INTEGER NOT NULL,
                OrderDate TEXT NOT NULL,
                TotalAmount REAL NOT NULL
            )",

            @"CREATE TABLE OrderItems (
                ItemId INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId INTEGER NOT NULL,
                ProductName TEXT NOT NULL,
                Quantity INTEGER NOT NULL,
                UnitPrice REAL NOT NULL,
                FOREIGN KEY (OrderId) REFERENCES Orders(OrderId)
            )",

            @"CREATE TABLE SelfReferencing (
                NodeId INTEGER PRIMARY KEY AUTOINCREMENT,
                ParentNodeId INTEGER NULL,
                Name TEXT NOT NULL,
                FOREIGN KEY (ParentNodeId) REFERENCES SelfReferencing(NodeId)
            )",

            @"CREATE TABLE UniqueConstraints (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT NOT NULL UNIQUE,
                Username TEXT NOT NULL UNIQUE
            )",

            @"CREATE TABLE NullabilityTest (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RequiredString TEXT NOT NULL,
                OptionalString TEXT NULL,
                RequiredInt INTEGER NOT NULL,
                OptionalInt INTEGER NULL,
                RequiredDecimal REAL NOT NULL DEFAULT 0.0,
                OptionalDecimal REAL NULL
            )"
        };

        foreach (var statement in statements)
        {
            var cmd = new SqliteCommand(statement, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void LoadedModel_ShouldNotBeNull()
    {
        _loadedModel.Should().NotBeNull();
    }

    [Fact]
    public void LoadedModel_ShouldContainAllTables()
    {
        var tableNames = _loadedModel!.Tables.Select(t => t.DbName).ToList();

        tableNames.Should().Contain("DataTypes");
        tableNames.Should().Contain("CompositePK");
        tableNames.Should().Contain("Orders");
        tableNames.Should().Contain("OrderItems");
        tableNames.Should().Contain("SelfReferencing");
        tableNames.Should().Contain("UniqueConstraints");
        tableNames.Should().Contain("NullabilityTest");
    }

    [Fact]
    public void DataTypesTable_ShouldHaveCorrectColumns()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "DataTypes");

        table.Columns.Should().HaveCount(11);

        var idCol = table.Columns.First(c => c.ColumnName == "Id");
        idCol.IsIdentity.Should().BeTrue("INTEGER PRIMARY KEY AUTOINCREMENT should be marked as identity");
        idCol.IsPrimaryKey.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();

        var stringCol = table.Columns.First(c => c.ColumnName == "StringCol");
        stringCol.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void CompositePKTable_ShouldHaveCompositePrimaryKey()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "CompositePK");

        var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).ToList();
        pkColumns.Should().HaveCount(2);
        pkColumns.Select(c => c.ColumnName).Should().Contain("TenantId");
        pkColumns.Select(c => c.ColumnName).Should().Contain("UserId");

        // Composite PK should NOT be identity
        pkColumns.Should().NotContain(c => c.IsIdentity);
    }

    [Fact]
    public void OrderItemsTable_ShouldHaveForeignKeyToOrders()
    {
        var orderItemsTable = _loadedModel!.Tables.First(t => t.DbName == "OrderItems");

        var orderIdCol = orderItemsTable.Columns.First(c => c.ColumnName == "OrderId");

        orderItemsTable.SingleLinks.Should().ContainKey("order");
    }

    [Fact]
    public void SelfReferencingTable_ShouldHaveSelfJoin()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "SelfReferencing");

        var parentCol = table.Columns.First(c => c.ColumnName == "ParentNodeId");
        parentCol.IsNullable.Should().BeTrue();

        table.SingleLinks.Should().ContainKey("parentNode");
    }

    [Fact]
    public void IdentityColumns_ShouldBeMarkedCorrectly()
    {
        var dataTypesTable = _loadedModel!.Tables.First(t => t.DbName == "DataTypes");
        var ordersTable = _loadedModel!.Tables.First(t => t.DbName == "Orders");
        var compositePKTable = _loadedModel!.Tables.First(t => t.DbName == "CompositePK");

        // INTEGER PRIMARY KEY AUTOINCREMENT = identity
        dataTypesTable.Columns.First(c => c.ColumnName == "Id").IsIdentity.Should().BeTrue();
        ordersTable.Columns.First(c => c.ColumnName == "OrderId").IsIdentity.Should().BeTrue();

        // Composite PK should NOT have identity
        compositePKTable.Columns.Should().NotContain(c => c.IsIdentity);
    }

    [Fact]
    public void NullabilityTest_ShouldCorrectlyIdentifyNullableColumns()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "NullabilityTest");

        table.Columns.First(c => c.ColumnName == "RequiredString").IsNullable.Should().BeFalse();
        table.Columns.First(c => c.ColumnName == "OptionalString").IsNullable.Should().BeTrue();
        table.Columns.First(c => c.ColumnName == "RequiredInt").IsNullable.Should().BeFalse();
        table.Columns.First(c => c.ColumnName == "OptionalInt").IsNullable.Should().BeTrue();
    }

    [Fact]
    public void AllTablesSchemasShouldBeMain()
    {
        foreach (var table in _loadedModel!.Tables)
        {
            table.TableSchema.Should().Be("main", $"SQLite tables should be in 'main' schema, but {table.DbName} is in {table.TableSchema}");
            // TableCatalog is a ColumnDto property, not IDbTable - skip catalog check
        }
    }
}
