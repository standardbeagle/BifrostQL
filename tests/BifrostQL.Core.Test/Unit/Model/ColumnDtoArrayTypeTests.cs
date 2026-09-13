using System.Data;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Model;

/// <summary>
/// <c>ColumnDto.FromReader</c> is the only production seam through which a Postgres array
/// column's element type reaches the dialect: information_schema reports <c>DATA_TYPE = 'ARRAY'</c>
/// for every array and the element lives in <c>UDT_NAME</c> (<c>_text</c>, <c>_int4</c>). The
/// dialect tests feed synthetic <c>text[]</c> strings, so without this file a reverted or
/// mis-keyed <c>ResolveDataType</c> would leave every array bare again while they stayed green.
/// </summary>
public sealed class ColumnDtoArrayTypeTests
{
    [Theory]
    [InlineData("_text", "text[]")]
    [InlineData("_int4", "integer[]")]
    [InlineData("_int8", "bigint[]")]
    [InlineData("_TEXT", "text[]")]
    public void Array_column_with_known_udt_element_resolves_to_element_array(string udtName, string expected)
    {
        var dto = ColumnDto.FromReader(Row(dataType: "ARRAY", udtName: udtName), new());
        dto.DataType.Should().Be(expected);
    }

    [Fact]
    public void Array_column_with_user_defined_element_stays_bare()
    {
        var dto = ColumnDto.FromReader(Row(dataType: "ARRAY", udtName: "_agtype"), new());
        dto.DataType.Should().Be("ARRAY", "a user-defined element type has no cast the dialect may emit");
    }

    [Fact]
    public void Array_column_with_null_udt_stays_bare()
    {
        var dto = ColumnDto.FromReader(Row(dataType: "ARRAY", udtName: null), new());
        dto.DataType.Should().Be("ARRAY");
    }

    [Fact]
    public void Reader_without_a_udt_column_keeps_the_raw_data_type()
    {
        // SQL Server / MySQL schema readers never select UDT_NAME.
        var dto = ColumnDto.FromReader(Row(dataType: "nvarchar", udtName: null, includeUdtColumn: false), new());
        dto.DataType.Should().Be("nvarchar");
    }

    [Fact]
    public void Non_array_column_ignores_udt_name()
    {
        var dto = ColumnDto.FromReader(Row(dataType: "text", udtName: "text"), new());
        dto.DataType.Should().Be("text");
    }

    private static IDataReader Row(string dataType, string? udtName, bool includeUdtColumn = true)
    {
        var table = new DataTable();
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("COLUMN_NAME", typeof(string));
        table.Columns.Add("DATA_TYPE", typeof(string));
        if (includeUdtColumn) table.Columns.Add("UDT_NAME", typeof(string));
        table.Columns.Add("CHARACTER_MAXIMUM_LENGTH", typeof(long));
        table.Columns.Add("NUMERIC_PRECISION", typeof(long));
        table.Columns.Add("NUMERIC_SCALE", typeof(long));
        table.Columns.Add("IS_NULLABLE", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("IS_IDENTITY", typeof(int));

        var row = table.NewRow();
        row["TABLE_CATALOG"] = "db";
        row["TABLE_SCHEMA"] = "public";
        row["TABLE_NAME"] = "t";
        row["COLUMN_NAME"] = "tags";
        row["DATA_TYPE"] = dataType;
        if (includeUdtColumn) row["UDT_NAME"] = (object?)udtName ?? DBNull.Value;
        row["CHARACTER_MAXIMUM_LENGTH"] = DBNull.Value;
        row["NUMERIC_PRECISION"] = DBNull.Value;
        row["NUMERIC_SCALE"] = DBNull.Value;
        row["IS_NULLABLE"] = "YES";
        row["ORDINAL_POSITION"] = 1;
        row["IS_IDENTITY"] = 0;
        table.Rows.Add(row);

        var reader = table.CreateDataReader();
        reader.Read().Should().BeTrue();
        return reader;
    }
}
