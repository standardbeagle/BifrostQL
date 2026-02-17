using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests for SqliteSchemaReader using in-memory SQLite databases.
/// Verifies PRAGMA-based schema reading, AUTOINCREMENT detection, type mapping,
/// foreign key discovery, and composite primary key handling.
/// </summary>
public sealed class SqliteSchemaReaderTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private readonly SqliteSchemaReader _reader = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await CreateTestSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task CreateTestSchemaAsync()
    {
        var statements = new[]
        {
            @"CREATE TABLE Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Description TEXT
            )",
            @"CREATE TABLE Products (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Price REAL NOT NULL,
                Stock INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                Data BLOB,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
            )",
            @"CREATE TABLE CompositePK (
                TenantId INTEGER NOT NULL,
                UserId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                PRIMARY KEY (TenantId, UserId)
            )",
            @"CREATE TABLE TypeShowcase (
                Id INTEGER PRIMARY KEY,
                TextCol TEXT,
                IntCol INTEGER,
                RealCol REAL,
                BlobCol BLOB,
                VarcharCol VARCHAR(100),
                BigIntCol BIGINT,
                SmallIntCol SMALLINT,
                TinyIntCol TINYINT,
                BoolCol BOOLEAN,
                DateTimeCol DATETIME,
                TimestampCol TIMESTAMP,
                JsonCol JSON,
                DecimalCol DECIMAL(10,2),
                FloatCol FLOAT,
                NoneCol NONE
            )",
            @"CREATE TABLE SelfRef (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ParentId INTEGER,
                Name TEXT NOT NULL,
                FOREIGN KEY (ParentId) REFERENCES SelfRef(Id)
            )",
            @"CREATE TABLE MultiFk (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL,
                AuthorId INTEGER NOT NULL,
                Title TEXT NOT NULL,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id),
                FOREIGN KEY (AuthorId) REFERENCES SelfRef(Id)
            )",
            @"CREATE VIEW ProductSummary AS
                SELECT p.Id, p.Name, c.Name AS CategoryName
                FROM Products p
                JOIN Categories c ON p.CategoryId = c.Id"
        };

        foreach (var sql in statements)
        {
            await using var cmd = new SqliteCommand(sql, _connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #region Table Discovery

    [Fact]
    public async Task ReadSchema_DiscoversTables()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var tableNames = schema.Tables.Select(t => t.DbName).ToList();
        tableNames.Should().Contain("Categories");
        tableNames.Should().Contain("Products");
        tableNames.Should().Contain("CompositePK");
        tableNames.Should().Contain("TypeShowcase");
        tableNames.Should().Contain("SelfRef");
        tableNames.Should().Contain("MultiFk");
    }

    [Fact]
    public async Task ReadSchema_ExcludesSqliteInternalTables()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        schema.Tables.Should().NotContain(t => t.DbName.StartsWith("sqlite_"));
    }

    [Fact]
    public async Task ReadSchema_DiscoverViews()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var view = schema.Tables.FirstOrDefault(t => t.DbName == "ProductSummary");
        view.Should().NotBeNull();
        ((DbTable)view!).TableType.Should().Be("VIEW");
    }

    [Fact]
    public async Task ReadSchema_TablesHaveBaseTableType()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var categoriesTable = schema.Tables.First(t => t.DbName == "Categories");
        ((DbTable)categoriesTable).TableType.Should().Be("BASE TABLE");
    }

    [Fact]
    public async Task ReadSchema_AllTablesUseMainSchema()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var table in schema.Tables)
        {
            table.TableSchema.Should().Be("main",
                $"SQLite table {table.DbName} should use 'main' schema");
        }
    }

    #endregion

    #region Column Discovery

    [Fact]
    public async Task ReadSchema_CategoriesTable_HasCorrectColumns()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        table.ColumnLookup.Should().ContainKey("Id");
        table.ColumnLookup.Should().ContainKey("Name");
        table.ColumnLookup.Should().ContainKey("Description");
        table.ColumnLookup.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReadSchema_ColumnsHaveCorrectOrdinalPositions()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        var ordinals = table.ColumnLookup.Values
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => c.OrdinalPosition)
            .ToList();

        ordinals.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ReadSchema_ColumnsHaveGraphQlNames()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        foreach (var col in table.ColumnLookup.Values)
        {
            col.GraphQlName.Should().NotBeNullOrWhiteSpace(
                $"Column {col.ColumnName} should have a GraphQL name");
        }
    }

    #endregion

    #region AUTOINCREMENT and Identity Detection

    [Fact]
    public async Task ReadSchema_IntegerPrimaryKey_IsIdentity()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        var idCol = table.ColumnLookup["Id"];
        idCol.IsIdentity.Should().BeTrue(
            "INTEGER PRIMARY KEY AUTOINCREMENT should be detected as identity");
        idCol.IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_IntegerPrimaryKeyWithoutAutoincrement_IsStillIdentity()
    {
        // SQLite treats INTEGER PRIMARY KEY (without AUTOINCREMENT) as rowid alias
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "TypeShowcase");

        var idCol = table.ColumnLookup["Id"];
        idCol.IsIdentity.Should().BeTrue(
            "INTEGER PRIMARY KEY without AUTOINCREMENT is still a rowid alias in SQLite");
    }

    [Fact]
    public async Task ReadSchema_CompositePK_NotIdentity()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "CompositePK");

        table.ColumnLookup["TenantId"].IsIdentity.Should().BeFalse(
            "Composite PK columns should not be marked as identity");
        table.ColumnLookup["UserId"].IsIdentity.Should().BeFalse(
            "Composite PK columns should not be marked as identity");
    }

    [Fact]
    public async Task ReadSchema_CompositePK_BothColumnsArePrimaryKey()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "CompositePK");

        table.ColumnLookup["TenantId"].IsPrimaryKey.Should().BeTrue();
        table.ColumnLookup["UserId"].IsPrimaryKey.Should().BeTrue();
        table.ColumnLookup["Name"].IsPrimaryKey.Should().BeFalse();
    }

    #endregion

    #region Nullability Detection

    [Fact]
    public async Task ReadSchema_NotNullColumns_DetectedCorrectly()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Products");

        table.ColumnLookup["Name"].IsNullable.Should().BeFalse();
        table.ColumnLookup["Price"].IsNullable.Should().BeFalse();
        table.ColumnLookup["Stock"].IsNullable.Should().BeFalse();
        table.ColumnLookup["CategoryId"].IsNullable.Should().BeFalse();
    }

    [Fact]
    public async Task ReadSchema_NullableColumns_DetectedCorrectly()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var products = schema.Tables.First(t => t.DbName == "Products");
        var categories = schema.Tables.First(t => t.DbName == "Categories");

        categories.ColumnLookup["Description"].IsNullable.Should().BeTrue();
        products.ColumnLookup["Data"].IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_PrimaryKeyColumn_NotNullable()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        table.ColumnLookup["Id"].IsNullable.Should().BeFalse();
    }

    #endregion

    #region Data Type Preservation

    [Fact]
    public async Task ReadSchema_PreservesColumnDataTypes()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "TypeShowcase");

        table.ColumnLookup["TextCol"].DataType.Should().Be("TEXT");
        table.ColumnLookup["IntCol"].DataType.Should().Be("INTEGER");
        table.ColumnLookup["RealCol"].DataType.Should().Be("REAL");
        table.ColumnLookup["BlobCol"].DataType.Should().Be("BLOB");
    }

    [Fact]
    public async Task ReadSchema_PreservesExtendedTypeNames()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "TypeShowcase");

        table.ColumnLookup["VarcharCol"].DataType.Should().Be("VARCHAR(100)");
        table.ColumnLookup["BigIntCol"].DataType.Should().Be("BIGINT");
        table.ColumnLookup["SmallIntCol"].DataType.Should().Be("SMALLINT");
        table.ColumnLookup["TinyIntCol"].DataType.Should().Be("TINYINT");
        table.ColumnLookup["BoolCol"].DataType.Should().Be("BOOLEAN");
        table.ColumnLookup["DateTimeCol"].DataType.Should().Be("DATETIME");
        table.ColumnLookup["TimestampCol"].DataType.Should().Be("TIMESTAMP");
        table.ColumnLookup["JsonCol"].DataType.Should().Be("JSON");
        table.ColumnLookup["DecimalCol"].DataType.Should().Be("DECIMAL(10,2)");
        table.ColumnLookup["FloatCol"].DataType.Should().Be("FLOAT");
        table.ColumnLookup["NoneCol"].DataType.Should().Be("NONE");
    }

    #endregion

    #region Foreign Key Discovery

    [Fact]
    public async Task ReadSchema_DiscoversForeignKeys()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var productsCategoryRef = new ColumnRef("main", "main", "Products", "CategoryId");
        schema.ColumnConstraints.Should().ContainKey(productsCategoryRef);

        var constraints = schema.ColumnConstraints[productsCategoryRef];
        constraints.Should().Contain(c => c.ConstraintType == "FOREIGN KEY");
    }

    [Fact]
    public async Task ReadSchema_PrimaryKeyConstraints_Discovered()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var categoriesIdRef = new ColumnRef("main", "main", "Categories", "Id");
        schema.ColumnConstraints.Should().ContainKey(categoriesIdRef);

        var constraints = schema.ColumnConstraints[categoriesIdRef];
        constraints.Should().Contain(c => c.ConstraintType == "PRIMARY KEY");
    }

    [Fact]
    public async Task ReadSchema_MultipleForeignKeys_AllDiscovered()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var categoryRef = new ColumnRef("main", "main", "MultiFk", "CategoryId");
        var authorRef = new ColumnRef("main", "main", "MultiFk", "AuthorId");

        schema.ColumnConstraints.Should().ContainKey(categoryRef);
        schema.ColumnConstraints.Should().ContainKey(authorRef);

        schema.ColumnConstraints[categoryRef]
            .Should().Contain(c => c.ConstraintType == "FOREIGN KEY");
        schema.ColumnConstraints[authorRef]
            .Should().Contain(c => c.ConstraintType == "FOREIGN KEY");
    }

    [Fact]
    public async Task ReadSchema_SelfReferencingForeignKey_Discovered()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        var parentRef = new ColumnRef("main", "main", "SelfRef", "ParentId");
        schema.ColumnConstraints.Should().ContainKey(parentRef);

        schema.ColumnConstraints[parentRef]
            .Should().Contain(c => c.ConstraintType == "FOREIGN KEY");
    }

    #endregion

    #region Column Catalog and Schema Metadata

    [Fact]
    public async Task ReadSchema_AllColumnsHaveMainCatalog()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var col in schema.RawColumns)
        {
            col.TableCatalog.Should().Be("main",
                $"Column {col.TableName}.{col.ColumnName} catalog should be 'main'");
        }
    }

    [Fact]
    public async Task ReadSchema_AllColumnsHaveMainSchema()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var col in schema.RawColumns)
        {
            col.TableSchema.Should().Be("main",
                $"Column {col.TableName}.{col.ColumnName} schema should be 'main'");
        }
    }

    [Fact]
    public async Task ReadSchema_ColumnRefsAreConsistent()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var col in schema.RawColumns)
        {
            col.ColumnRef.Should().NotBeNull();
            col.ColumnRef.Catalog.Should().Be("main");
            col.ColumnRef.Schema.Should().Be("main");
            col.ColumnRef.Table.Should().Be(col.TableName);
            col.ColumnRef.Column.Should().Be(col.ColumnName);
        }
    }

    #endregion

    #region GraphQL Name Generation

    [Fact]
    public async Task ReadSchema_TablesHaveGraphQlNames()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var table in schema.Tables)
        {
            table.GraphQlName.Should().NotBeNullOrWhiteSpace(
                $"Table {table.DbName} should have a GraphQL name");
        }
    }

    [Fact]
    public async Task ReadSchema_TablesHaveNormalizedNames()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);

        foreach (var table in schema.Tables)
        {
            ((DbTable)table).NormalizedName.Should().NotBeNullOrWhiteSpace(
                $"Table {table.DbName} should have a normalized name");
        }
    }

    [Fact]
    public async Task ReadSchema_GraphQlLookupIsPopulated()
    {
        var schema = await _reader.ReadSchemaAsync(_connection);
        var table = schema.Tables.First(t => t.DbName == "Categories");

        table.GraphQlLookup.Should().NotBeEmpty();
        table.GraphQlLookup.Should().HaveCount(table.ColumnLookup.Count);
    }

    #endregion

    #region Empty Database

    [Fact]
    public async Task ReadSchema_EmptyDatabase_ReturnsEmptySchema()
    {
        await using var emptyConn = new SqliteConnection("Data Source=:memory:");
        await emptyConn.OpenAsync();

        var schema = await _reader.ReadSchemaAsync(emptyConn);

        schema.Tables.Should().BeEmpty();
        schema.RawColumns.Should().BeEmpty();
    }

    #endregion
}
