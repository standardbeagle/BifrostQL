using BifrostQL.Core.Model;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using BifrostQL.MySql;
using FluentAssertions;
using MySqlConnector;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// Tests that DbModelLoader correctly reads MySQL schemas into DbModel.
/// Only runs when BIFROST_TEST_MYSQL env var is set.
/// </summary>
[Collection("MySqlSchemaLoading")]
public class MySqlSchemaLoadingTests : IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;
    private IDbModel? _loadedModel;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_MYSQL environment variable not set");
            return;
        }

        _testDbName = $"bifrost_schema_test_{Guid.NewGuid():N}";

        // Create test database
        await using var masterConn = new MySqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new MySqlCommand($"CREATE DATABASE `{_testDbName}`", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new MySqlConnectionStringBuilder(masterConnString)
        {
            Database = _testDbName
        };
        _connectionString = builder.ConnectionString;

        // Create complex schema
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await CreateComplexSchemaAsync(conn);

        // Load schema using DbModelLoader
        var factory = new MySqlDbConnFactory(_connectionString);
        var metadataLoader = new MetadataLoader(Array.Empty<string>());
        var loader = new DbModelLoader(factory, metadataLoader);
        _loadedModel = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
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
        catch
        {
            // Best effort cleanup
        }
    }

    private static async Task CreateComplexSchemaAsync(MySqlConnection conn)
    {
        var statements = new[]
        {
            @"CREATE TABLE DataTypes (
                Id INT AUTO_INCREMENT PRIMARY KEY,
                StringCol VARCHAR(100) NOT NULL,
                IntCol INT NOT NULL,
                BigIntCol BIGINT NULL,
                DecimalCol DECIMAL(18,2) NOT NULL,
                FloatCol DOUBLE NULL,
                BoolCol BOOLEAN NOT NULL,
                DateCol DATE NULL,
                DateTimeCol DATETIME NOT NULL,
                TimeCol TIME NULL,
                TextCol TEXT NULL
            )",

            @"CREATE TABLE CompositePK (
                TenantId INT NOT NULL,
                UserId INT NOT NULL,
                Name VARCHAR(50) NOT NULL,
                PRIMARY KEY (TenantId, UserId)
            )",

            @"CREATE TABLE Orders (
                OrderId INT AUTO_INCREMENT PRIMARY KEY,
                CustomerId INT NOT NULL,
                OrderDate DATETIME NOT NULL,
                TotalAmount DECIMAL(18,2) NOT NULL
            )",

            @"CREATE TABLE OrderItems (
                ItemId INT AUTO_INCREMENT PRIMARY KEY,
                OrderId INT NOT NULL,
                ProductName VARCHAR(100) NOT NULL,
                Quantity INT NOT NULL,
                UnitPrice DECIMAL(18,2) NOT NULL,
                CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES Orders(OrderId)
            )",

            @"CREATE TABLE SelfReferencing (
                NodeId INT AUTO_INCREMENT PRIMARY KEY,
                ParentNodeId INT NULL,
                Name VARCHAR(50) NOT NULL,
                CONSTRAINT FK_SelfRef_Parent FOREIGN KEY (ParentNodeId) REFERENCES SelfReferencing(NodeId)
            )",

            @"CREATE TABLE UniqueConstraints (
                Id INT AUTO_INCREMENT PRIMARY KEY,
                Email VARCHAR(100) NOT NULL,
                Username VARCHAR(50) NOT NULL,
                CONSTRAINT UQ_Email UNIQUE (Email),
                CONSTRAINT UQ_Username UNIQUE (Username)
            )",

            @"CREATE TABLE NullabilityTest (
                Id INT AUTO_INCREMENT PRIMARY KEY,
                RequiredString VARCHAR(100) NOT NULL,
                OptionalString VARCHAR(100) NULL,
                RequiredInt INT NOT NULL,
                OptionalInt INT NULL,
                RequiredDecimal DECIMAL(10,2) NOT NULL DEFAULT 0.0,
                OptionalDecimal DECIMAL(10,2) NULL
            )"
        };

        foreach (var statement in statements)
        {
            var cmd = new MySqlCommand(statement, conn);
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
        idCol.IsIdentity.Should().BeTrue();
        idCol.IsPrimaryKey.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();

        var stringCol = table.Columns.First(c => c.ColumnName == "StringCol");
        stringCol.IsNullable.Should().BeFalse();

        var decimalCol = table.Columns.First(c => c.ColumnName == "DecimalCol");
        decimalCol.EffectiveDataType.Should().Be("decimal");
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

        dataTypesTable.Columns.First(c => c.ColumnName == "Id").IsIdentity.Should().BeTrue();
        ordersTable.Columns.First(c => c.ColumnName == "OrderId").IsIdentity.Should().BeTrue();
    }
}
