using BifrostQL.Core.Model;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// Tests that DbModelLoader correctly reads SQL Server schemas into DbModel.
/// Uses real database with complex schema configurations.
/// </summary>
[Collection("SqlServerSchemaLoading")]
public class SqlServerSchemaLoadingTests : IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;
    private IDbModel? _loadedModel;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER")
            ?? "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

        _testDbName = $"BifrostSchemaTest_{Guid.NewGuid():N}";

        // Create test database
        await using var masterConn = new SqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new SqlCommand($"CREATE DATABASE [{_testDbName}]", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new SqlConnectionStringBuilder(masterConnString)
        {
            InitialCatalog = _testDbName
        };
        _connectionString = builder.ConnectionString;

        // Create complex schema
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await CreateComplexSchemaAsync(conn);

        // Load schema using DbModelLoader
        var factory = new DbConnFactory(_connectionString);
        var metadataLoader = new MetadataLoader();
        var loader = new DbModelLoader(factory, metadataLoader);
        _loadedModel = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
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
        catch
        {
            // Best effort cleanup
        }
    }

    private static async Task CreateComplexSchemaAsync(SqlConnection conn)
    {
        var ddl = @"
-- Table with identity, various data types
CREATE TABLE DataTypes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    StringCol NVARCHAR(100) NOT NULL,
    IntCol INT NOT NULL,
    BigIntCol BIGINT NULL,
    DecimalCol DECIMAL(18,2) NOT NULL,
    FloatCol FLOAT NULL,
    BitCol BIT NOT NULL,
    DateCol DATE NULL,
    DateTimeCol DATETIME2 NOT NULL,
    TimeCol TIME NULL,
    UniqueIdCol UNIQUEIDENTIFIER NULL,
    BinaryCol VARBINARY(MAX) NULL
);

-- Table with composite primary key
CREATE TABLE CompositePK (
    TenantId INT NOT NULL,
    UserId INT NOT NULL,
    Name NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_CompositePK PRIMARY KEY (TenantId, UserId)
);

-- Table with foreign keys
CREATE TABLE Orders (
    OrderId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId INT NOT NULL,
    OrderDate DATETIME2 NOT NULL,
    TotalAmount DECIMAL(18,2) NOT NULL
);

CREATE TABLE OrderItems (
    ItemId INT IDENTITY(1,1) PRIMARY KEY,
    OrderId INT NOT NULL,
    ProductName NVARCHAR(100) NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(18,2) NOT NULL,
    CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES Orders(OrderId)
);

-- Table with multiple foreign keys pointing to same table
CREATE TABLE SelfReferencing (
    NodeId INT IDENTITY(1,1) PRIMARY KEY,
    ParentNodeId INT NULL,
    Name NVARCHAR(50) NOT NULL,
    CONSTRAINT FK_SelfRef_Parent FOREIGN KEY (ParentNodeId) REFERENCES SelfReferencing(NodeId)
);

-- Table with unique constraints
CREATE TABLE UniqueConstraints (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Email NVARCHAR(100) NOT NULL,
    Username NVARCHAR(50) NOT NULL,
    CONSTRAINT UQ_Email UNIQUE (Email),
    CONSTRAINT UQ_Username UNIQUE (Username)
);

-- Table with nullable and non-nullable columns
CREATE TABLE NullabilityTest (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    RequiredString NVARCHAR(100) NOT NULL,
    OptionalString NVARCHAR(100) NULL,
    RequiredInt INT NOT NULL,
    OptionalInt INT NULL,
    RequiredDecimal DECIMAL(10,2) NOT NULL DEFAULT 0.0,
    OptionalDecimal DECIMAL(10,2) NULL
);

-- Schema-qualified tables (dbo vs custom schema)
CREATE SCHEMA TestSchema;
GO

CREATE TABLE TestSchema.CustomSchemaTable (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Data NVARCHAR(50) NOT NULL
);
";

        foreach (var batch in ddl.Split("GO", StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            var cmd = new SqlCommand(batch, conn);
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
        tableNames.Should().Contain("TestSchema.CustomSchemaTable");
    }

    [Fact]
    public void DataTypesTable_ShouldHaveCorrectColumns()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "DataTypes");

        table.Columns.Should().HaveCount(12);

        var idCol = table.Columns.First(c => c.ColumnName == "Id");
        idCol.IsIdentity.Should().BeTrue();
        idCol.IsPrimaryKey.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();
        idCol.EffectiveDataType.Should().Be("int");

        var stringCol = table.Columns.First(c => c.ColumnName == "StringCol");
        stringCol.IsNullable.Should().BeFalse();
        stringCol.EffectiveDataType.Should().Be("nvarchar");
        stringCol.CharacterMaximumLength.Should().Be(100);

        var nullableBigInt = table.Columns.First(c => c.ColumnName == "BigIntCol");
        nullableBigInt.IsNullable.Should().BeTrue();
        nullableBigInt.EffectiveDataType.Should().Be("bigint");

        var decimalCol = table.Columns.First(c => c.ColumnName == "DecimalCol");
        decimalCol.NumericPrecision.Should().Be(18);
        decimalCol.NumericScale.Should().Be(2);
    }

    [Fact]
    public void CompositePKTable_ShouldHaveCompositePrimaryKey()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "CompositePK");

        var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).ToList();
        pkColumns.Should().HaveCount(2);
        pkColumns.Select(c => c.ColumnName).Should().Contain("TenantId");
        pkColumns.Select(c => c.ColumnName).Should().Contain("UserId");
    }

    [Fact]
    public void OrderItemsTable_ShouldHaveForeignKeyToOrders()
    {
        var orderItemsTable = _loadedModel!.Tables.First(t => t.DbName == "OrderItems");

        var orderIdCol = orderItemsTable.Columns.First(c => c.ColumnName == "OrderId");

        // Should have a single link to Orders table
        orderItemsTable.SingleLinks.Should().ContainKey("order");
        var link = orderItemsTable.SingleLinks["order"];
        link.ParentTable.DbName.Should().Be("Orders");
    }

    [Fact]
    public void SelfReferencingTable_ShouldHaveSelfJoin()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "SelfReferencing");

        var parentCol = table.Columns.First(c => c.ColumnName == "ParentNodeId");
        parentCol.IsNullable.Should().BeTrue();

        // Should have a self-referencing single link
        table.SingleLinks.Should().ContainKey("parentNode");
        var link = table.SingleLinks["parentNode"];
        link.ParentTable.DbName.Should().Be("SelfReferencing");
        link.ChildTable.DbName.Should().Be("SelfReferencing");
    }

    [Fact]
    public void NullabilityTest_ShouldCorrectlyIdentifyNullableColumns()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName == "NullabilityTest");

        table.Columns.First(c => c.ColumnName == "RequiredString").IsNullable.Should().BeFalse();
        table.Columns.First(c => c.ColumnName == "OptionalString").IsNullable.Should().BeTrue();
        table.Columns.First(c => c.ColumnName == "RequiredInt").IsNullable.Should().BeFalse();
        table.Columns.First(c => c.ColumnName == "OptionalInt").IsNullable.Should().BeTrue();
        table.Columns.First(c => c.ColumnName == "RequiredDecimal").IsNullable.Should().BeFalse();
        table.Columns.First(c => c.ColumnName == "OptionalDecimal").IsNullable.Should().BeTrue();
    }

    [Fact]
    public void CustomSchemaTable_ShouldBeLoadedWithSchemaQualification()
    {
        var table = _loadedModel!.Tables.FirstOrDefault(t => t.DbName == "TestSchema.CustomSchemaTable");

        table.Should().NotBeNull();
        table!.TableSchema.Should().Be("TestSchema");
        table.DbName.Should().Be("CustomSchemaTable");
    }

    [Fact]
    public void IdentityColumns_ShouldBeMarkedCorrectly()
    {
        var dataTypesTable = _loadedModel!.Tables.First(t => t.DbName == "DataTypes");
        var ordersTable = _loadedModel!.Tables.First(t => t.DbName == "Orders");
        var compositePKTable = _loadedModel!.Tables.First(t => t.DbName == "CompositePK");

        dataTypesTable.Columns.First(c => c.ColumnName == "Id").IsIdentity.Should().BeTrue();
        ordersTable.Columns.First(c => c.ColumnName == "OrderId").IsIdentity.Should().BeTrue();

        // CompositePK has no identity columns
        compositePKTable.Columns.Should().NotContain(c => c.IsIdentity);
    }

    [Fact]
    public void OrdersTable_ShouldHaveMultiLinkToOrderItems()
    {
        var ordersTable = _loadedModel!.Tables.First(t => t.DbName == "Orders");

        // Should have a multi-link (one-to-many) to OrderItems
        ordersTable.MultiLinks.Should().ContainKey("orderItems");
        var link = ordersTable.MultiLinks["orderItems"];
        link.ChildTable.DbName.Should().Be("OrderItems");
        link.ParentTable.DbName.Should().Be("Orders");
    }

    [Fact]
    public void AllTables_ShouldHaveKeyColumns()
    {
        foreach (var table in _loadedModel!.Tables)
        {
            table.KeyColumns.Should().NotBeEmpty($"Table {table.DbName} should have at least one key column");
        }
    }
}
