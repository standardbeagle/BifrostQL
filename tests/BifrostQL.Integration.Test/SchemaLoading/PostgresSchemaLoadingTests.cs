using BifrostQL.Core.Model;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using BifrostQL.Ngsql;
using FluentAssertions;
using Npgsql;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// Tests that DbModelLoader correctly reads PostgreSQL schemas into DbModel.
/// Only runs when BIFROST_TEST_POSTGRES env var is set.
/// </summary>
[Collection("PostgresSchemaLoading")]
public class PostgresSchemaLoadingTests : IAsyncLifetime
{
    private string? _connectionString;
    private string? _testDbName;
    private IDbModel? _loadedModel;

    public async Task InitializeAsync()
    {
        var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        if (masterConnString == null)
        {
            Skip.If(true, "BIFROST_TEST_POSTGRES environment variable not set");
            return;
        }

        _testDbName = $"bifrost_schema_test_{Guid.NewGuid():N}";

        // Create test database
        await using var masterConn = new NpgsqlConnection(masterConnString);
        await masterConn.OpenAsync();
        var createCmd = new NpgsqlCommand($"CREATE DATABASE {_testDbName}", masterConn);
        await createCmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(masterConnString)
        {
            Database = _testDbName
        };
        _connectionString = builder.ConnectionString;

        // Create complex schema
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await CreateComplexSchemaAsync(conn);

        // Load schema using DbModelLoader
        var factory = new PostgresDbConnFactory(_connectionString);
        var metadataLoader = new MetadataLoader();
        var loader = new DbModelLoader(factory, metadataLoader);
        _loadedModel = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        if (_testDbName == null) return;

        try
        {
            var masterConnString = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
            if (masterConnString == null) return;

            await using var conn = new NpgsqlConnection(masterConnString);
            await conn.OpenAsync();

            // Terminate connections before dropping
            var terminateCmd = new NpgsqlCommand($@"
                SELECT pg_terminate_backend(pg_stat_activity.pid)
                FROM pg_stat_activity
                WHERE pg_stat_activity.datname = '{_testDbName}'
                AND pid <> pg_backend_pid()", conn);
            await terminateCmd.ExecuteNonQueryAsync();

            var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_testDbName}", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static async Task CreateComplexSchemaAsync(NpgsqlConnection conn)
    {
        var ddl = @"
-- Table with serial (identity), various data types
CREATE TABLE datatypes (
    id SERIAL PRIMARY KEY,
    string_col VARCHAR(100) NOT NULL,
    int_col INTEGER NOT NULL,
    bigint_col BIGINT NULL,
    decimal_col DECIMAL(18,2) NOT NULL,
    float_col DOUBLE PRECISION NULL,
    bool_col BOOLEAN NOT NULL,
    date_col DATE NULL,
    timestamp_col TIMESTAMP NOT NULL,
    time_col TIME NULL,
    uuid_col UUID NULL,
    json_col JSONB NULL
);

-- Table with composite primary key
CREATE TABLE composite_pk (
    tenant_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    name VARCHAR(50) NOT NULL,
    CONSTRAINT pk_composite_pk PRIMARY KEY (tenant_id, user_id)
);

-- Tables with foreign keys
CREATE TABLE orders (
    order_id SERIAL PRIMARY KEY,
    customer_id INTEGER NOT NULL,
    order_date TIMESTAMP NOT NULL,
    total_amount DECIMAL(18,2) NOT NULL
);

CREATE TABLE order_items (
    item_id SERIAL PRIMARY KEY,
    order_id INTEGER NOT NULL,
    product_name VARCHAR(100) NOT NULL,
    quantity INTEGER NOT NULL,
    unit_price DECIMAL(18,2) NOT NULL,
    CONSTRAINT fk_order_items_orders FOREIGN KEY (order_id) REFERENCES orders(order_id)
);

-- Self-referencing table
CREATE TABLE self_referencing (
    node_id SERIAL PRIMARY KEY,
    parent_node_id INTEGER NULL,
    name VARCHAR(50) NOT NULL,
    CONSTRAINT fk_self_ref_parent FOREIGN KEY (parent_node_id) REFERENCES self_referencing(node_id)
);

-- Table with unique constraints
CREATE TABLE unique_constraints (
    id SERIAL PRIMARY KEY,
    email VARCHAR(100) NOT NULL,
    username VARCHAR(50) NOT NULL,
    CONSTRAINT uq_email UNIQUE (email),
    CONSTRAINT uq_username UNIQUE (username)
);

-- Table with nullable and non-nullable columns
CREATE TABLE nullability_test (
    id SERIAL PRIMARY KEY,
    required_string VARCHAR(100) NOT NULL,
    optional_string VARCHAR(100) NULL,
    required_int INTEGER NOT NULL,
    optional_int INTEGER NULL,
    required_decimal DECIMAL(10,2) NOT NULL DEFAULT 0.0,
    optional_decimal DECIMAL(10,2) NULL
);

-- Custom schema
CREATE SCHEMA test_schema;

CREATE TABLE test_schema.custom_schema_table (
    id SERIAL PRIMARY KEY,
    data VARCHAR(50) NOT NULL
);
";

        var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public void LoadedModel_ShouldNotBeNull()
    {
        _loadedModel.Should().NotBeNull();
    }

    [Fact]
    public void LoadedModel_ShouldContainAllTables()
    {
        var tableNames = _loadedModel!.Tables.Select(t => t.DbName.ToLowerInvariant()).ToList();

        tableNames.Should().Contain("datatypes");
        tableNames.Should().Contain("composite_pk");
        tableNames.Should().Contain("orders");
        tableNames.Should().Contain("order_items");
        tableNames.Should().Contain("self_referencing");
        tableNames.Should().Contain("unique_constraints");
        tableNames.Should().Contain("nullability_test");
        tableNames.Should().Contain("custom_schema_table");
    }

    [Fact]
    public void DataTypesTable_ShouldHaveCorrectColumns()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "datatypes");

        table.Columns.Should().HaveCount(12);

        var idCol = table.Columns.First(c => c.ColumnName == "id");
        idCol.IsIdentity.Should().BeTrue();
        idCol.IsPrimaryKey.Should().BeTrue();
        idCol.IsNullable.Should().BeFalse();

        var stringCol = table.Columns.First(c => c.ColumnName == "string_col");
        stringCol.IsNullable.Should().BeFalse();
        stringCol.CharacterMaximumLength.Should().Be(100);

        var decimalCol = table.Columns.First(c => c.ColumnName == "decimal_col");
        decimalCol.NumericPrecision.Should().Be(18);
        decimalCol.NumericScale.Should().Be(2);
    }

    [Fact]
    public void CompositePKTable_ShouldHaveCompositePrimaryKey()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "composite_pk");

        var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).ToList();
        pkColumns.Should().HaveCount(2);
        pkColumns.Select(c => c.ColumnName).Should().Contain("tenant_id");
        pkColumns.Select(c => c.ColumnName).Should().Contain("user_id");
    }

    [Fact]
    public void OrderItemsTable_ShouldHaveForeignKeyToOrders()
    {
        var orderItemsTable = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "order_items");

        var orderIdCol = orderItemsTable.Columns.First(c => c.ColumnName == "order_id");

        orderItemsTable.SingleLinks.Should().ContainKey("order");
    }

    [Fact]
    public void SelfReferencingTable_ShouldHaveSelfJoin()
    {
        var table = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "self_referencing");

        var parentCol = table.Columns.First(c => c.ColumnName == "parent_node_id");
        parentCol.IsNullable.Should().BeTrue();

        table.SingleLinks.Should().ContainKey("parentNode");
    }

    [Fact]
    public void IdentityColumns_ShouldBeMarkedCorrectly()
    {
        var dataTypesTable = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "datatypes");
        var ordersTable = _loadedModel!.Tables.First(t => t.DbName.ToLowerInvariant() == "orders");

        dataTypesTable.Columns.First(c => c.ColumnName == "id").IsIdentity.Should().BeTrue();
        ordersTable.Columns.First(c => c.ColumnName == "order_id").IsIdentity.Should().BeTrue();
    }
}
