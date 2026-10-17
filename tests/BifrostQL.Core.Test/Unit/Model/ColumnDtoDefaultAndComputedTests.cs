using System.Data;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Model;

/// <summary>
/// <c>ColumnDto.FromReader</c> is the seam the SQL Server, Postgres and MySQL readers
/// share: each derives <c>HAS_DEFAULT</c> / <c>IS_COMPUTED</c> in its own catalog SQL and
/// this mapping turns them into the flags insert requiredness reads. Postgres and MySQL
/// alias the columns in lower case, so the mapping must accept either spelling.
/// </summary>
public sealed class ColumnDtoDefaultAndComputedTests
{
    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(1, 0, true, false)]
    [InlineData(0, 1, false, true)]
    [InlineData(1, 1, true, true)]
    public void FromReader_maps_default_and_computed_flags(int hasDefault, int isComputed, bool expectedDefault, bool expectedComputed)
    {
        var dto = ColumnDto.FromReader(Row(hasDefault, isComputed, upperCase: true), new());

        dto.HasDefault.Should().Be(expectedDefault);
        dto.IsComputed.Should().Be(expectedComputed);
    }

    [Fact]
    public void FromReader_reads_lower_case_aliases()
    {
        var dto = ColumnDto.FromReader(Row(hasDefault: 1, isComputed: 1, upperCase: false), new());

        dto.HasDefault.Should().BeTrue();
        dto.IsComputed.Should().BeTrue();
    }

    [Fact]
    public void WithGraphQlName_keeps_the_default_flag()
    {
        var dto = ColumnDto.FromReader(Row(hasDefault: 1, isComputed: 0, upperCase: true), new());

        dto.WithGraphQlName("renamed").HasDefault.Should().BeTrue();
    }

    private static IDataReader Row(int hasDefault, int isComputed, bool upperCase)
    {
        string N(string name) => upperCase ? name : name.ToLowerInvariant();
        var table = new DataTable();
        foreach (var name in new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "DATA_TYPE", "IS_NULLABLE" })
            table.Columns.Add(N(name), typeof(string));
        foreach (var name in new[] { "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE" })
            table.Columns.Add(N(name), typeof(long));
        foreach (var name in new[] { "ORDINAL_POSITION", "IS_IDENTITY", "IS_COMPUTED", "HAS_DEFAULT" })
            table.Columns.Add(N(name), typeof(int));

        var row = table.NewRow();
        row[N("TABLE_CATALOG")] = "db";
        row[N("TABLE_SCHEMA")] = "public";
        row[N("TABLE_NAME")] = "t";
        row[N("COLUMN_NAME")] = "status";
        row[N("DATA_TYPE")] = "text";
        row[N("IS_NULLABLE")] = "NO";
        row[N("CHARACTER_MAXIMUM_LENGTH")] = DBNull.Value;
        row[N("NUMERIC_PRECISION")] = DBNull.Value;
        row[N("NUMERIC_SCALE")] = DBNull.Value;
        row[N("ORDINAL_POSITION")] = 1;
        row[N("IS_IDENTITY")] = 0;
        row[N("IS_COMPUTED")] = isComputed;
        row[N("HAS_DEFAULT")] = hasDefault;
        table.Rows.Add(row);

        var reader = table.CreateDataReader();
        reader.Read().Should().BeTrue();
        return reader;
    }
}
