using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests that verify SqliteTypeMapper produces correct GraphQL types
/// for columns read from actual in-memory SQLite databases. Validates that the
/// schema reader + type mapper pipeline maps SQLite types correctly end-to-end.
/// </summary>
public sealed class SqliteTypeMapperIntegrationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SchemaData _schema = null!;
    private readonly SqliteSchemaReader _reader = new();
    private readonly SqliteTypeMapper _mapper = SqliteTypeMapper.Instance;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using var cmd = new SqliteCommand(@"
            CREATE TABLE TypeMapping (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TextCol TEXT,
                IntegerCol INTEGER,
                RealCol REAL,
                BlobCol BLOB,
                VarcharCol VARCHAR(255),
                NvarcharCol NVARCHAR(100),
                CharCol CHAR(10),
                ClobCol CLOB,
                BigIntCol BIGINT,
                SmallIntCol SMALLINT,
                TinyIntCol TINYINT,
                MediumIntCol MEDIUMINT,
                BooleanCol BOOLEAN,
                BitCol BIT,
                DateTimeCol DATETIME,
                TimestampCol TIMESTAMP,
                JsonCol JSON,
                DecimalCol DECIMAL(18,2),
                NumericCol NUMERIC,
                DoubleCol DOUBLE,
                FloatCol FLOAT,
                NoneCol NONE
            )", _connection);
        await cmd.ExecuteNonQueryAsync();

        _schema = await _reader.ReadSchemaAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private ColumnDto GetColumn(string name)
    {
        var table = _schema.Tables.First(t => t.DbName == "TypeMapping");
        return table.ColumnLookup[name];
    }

    #region Integer Type Mappings

    [Fact]
    public void IntegerPrimaryKey_MapsToInt()
    {
        var col = GetColumn("Id");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Int");
    }

    [Fact]
    public void IntegerColumn_MapsToInt()
    {
        var col = GetColumn("IntegerCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Int");
    }

    [Fact]
    public void MediumIntColumn_MapsToInt()
    {
        var col = GetColumn("MediumIntCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Int");
    }

    [Fact]
    public void BigIntColumn_MapsToBigInt()
    {
        var col = GetColumn("BigIntCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("BigInt");
    }

    [Fact]
    public void SmallIntColumn_MapsToShort()
    {
        var col = GetColumn("SmallIntCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Short");
    }

    [Fact]
    public void TinyIntColumn_MapsToByte()
    {
        var col = GetColumn("TinyIntCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Byte");
    }

    #endregion

    #region Real/Float Type Mappings

    [Fact]
    public void RealColumn_MapsToFloat()
    {
        var col = GetColumn("RealCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Float");
    }

    [Fact]
    public void DoubleColumn_MapsToFloat()
    {
        var col = GetColumn("DoubleCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Float");
    }

    [Fact]
    public void FloatColumn_MapsToFloat()
    {
        var col = GetColumn("FloatCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Float");
    }

    [Fact]
    public void DecimalColumn_MapsToDecimal()
    {
        var col = GetColumn("DecimalCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Decimal");
    }

    [Fact]
    public void NumericColumn_MapsToDecimal()
    {
        var col = GetColumn("NumericCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Decimal");
    }

    #endregion

    #region Text Type Mappings

    [Fact]
    public void TextColumn_MapsToString()
    {
        var col = GetColumn("TextCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    [Fact]
    public void VarcharColumn_MapsToString()
    {
        var col = GetColumn("VarcharCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    [Fact]
    public void NvarcharColumn_MapsToString()
    {
        var col = GetColumn("NvarcharCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    [Fact]
    public void CharColumn_MapsToString()
    {
        var col = GetColumn("CharCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    [Fact]
    public void ClobColumn_MapsToString()
    {
        var col = GetColumn("ClobCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    #endregion

    #region Special Type Mappings

    [Fact]
    public void BlobColumn_MapsToString()
    {
        var col = GetColumn("BlobCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    [Fact]
    public void BooleanColumn_MapsToBoolean()
    {
        var col = GetColumn("BooleanCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Boolean");
    }

    [Fact]
    public void BitColumn_MapsToBoolean()
    {
        var col = GetColumn("BitCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("Boolean");
    }

    [Fact]
    public void DateTimeColumn_MapsToDateTime()
    {
        var col = GetColumn("DateTimeCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("DateTime");
    }

    [Fact]
    public void TimestampColumn_MapsToDateTime()
    {
        var col = GetColumn("TimestampCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("DateTime");
    }

    [Fact]
    public void JsonColumn_MapsToJSON()
    {
        var col = GetColumn("JsonCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("JSON");
    }

    [Fact]
    public void NoneColumn_MapsToString()
    {
        var col = GetColumn("NoneCol");
        _mapper.GetGraphQlType(col.DataType).Should().Be("String");
    }

    #endregion

    #region Insert Type Name for DateTime Columns

    [Fact]
    public void DateTimeColumn_InsertType_UsesString()
    {
        var col = GetColumn("DateTimeCol");
        _mapper.GetGraphQlInsertTypeName(col.DataType, col.IsNullable)
            .Should().Be("String");
    }

    [Fact]
    public void TimestampColumn_InsertType_UsesString()
    {
        var col = GetColumn("TimestampCol");
        _mapper.GetGraphQlInsertTypeName(col.DataType, col.IsNullable)
            .Should().Be("String");
    }

    [Fact]
    public void IntegerColumn_InsertType_PreservesType()
    {
        var col = GetColumn("IntegerCol");
        _mapper.GetGraphQlInsertTypeName(col.DataType, col.IsNullable)
            .Should().Be("Int");
    }

    #endregion

    #region Filter Input Type Names

    [Fact]
    public void IntegerColumn_FilterType_ReturnsCorrectFormat()
    {
        var col = GetColumn("IntegerCol");
        _mapper.GetFilterInputTypeName(col.DataType)
            .Should().Be("FilterTypeIntInput");
    }

    [Fact]
    public void TextColumn_FilterType_ReturnsCorrectFormat()
    {
        var col = GetColumn("TextCol");
        _mapper.GetFilterInputTypeName(col.DataType)
            .Should().Be("FilterTypeStringInput");
    }

    [Fact]
    public void BooleanColumn_FilterType_ReturnsCorrectFormat()
    {
        var col = GetColumn("BooleanCol");
        _mapper.GetFilterInputTypeName(col.DataType)
            .Should().Be("FilterTypeBooleanInput");
    }

    [Fact]
    public void DateTimeColumn_FilterType_ReturnsCorrectFormat()
    {
        var col = GetColumn("DateTimeCol");
        _mapper.GetFilterInputTypeName(col.DataType)
            .Should().Be("FilterTypeDateTimeInput");
    }

    #endregion

    #region IsSupported

    [Theory]
    [InlineData("IntegerCol", true)]
    [InlineData("TextCol", true)]
    [InlineData("RealCol", true)]
    [InlineData("BlobCol", true)]
    [InlineData("BooleanCol", true)]
    [InlineData("JsonCol", true)]
    [InlineData("DateTimeCol", true)]
    public void IsSupported_ForActualColumnTypes(string columnName, bool expected)
    {
        var col = GetColumn(columnName);
        _mapper.IsSupported(col.DataType).Should().Be(expected);
    }

    #endregion

    #region Nullability in Type Names

    [Fact]
    public void NonNullableColumn_TypeNameHasBang()
    {
        // Id is NOT NULL (primary key)
        var col = GetColumn("Id");
        _mapper.GetGraphQlTypeName(col.DataType, col.IsNullable)
            .Should().EndWith("!");
    }

    [Fact]
    public void NullableColumn_TypeNameHasNoBang()
    {
        // TextCol is nullable
        var col = GetColumn("TextCol");
        _mapper.GetGraphQlTypeName(col.DataType, col.IsNullable)
            .Should().NotEndWith("!");
    }

    #endregion
}
