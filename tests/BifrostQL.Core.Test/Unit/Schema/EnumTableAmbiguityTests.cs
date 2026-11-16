using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Two schemas may each define an enum lookup table with the same bare name. The enum
/// map is keyed by schema-qualified table identity, so each table emits its own enum
/// type built from its own rows. (Before the map was schema-qualified, the bare name
/// could only bind one of them, and schema generation failed fast instead of guessing.)
/// </summary>
public sealed class EnumTableAmbiguityTests
{
    private static DbTable EnumTable(string schema)
    {
        var columns = new[]
        {
            new ColumnDto { ColumnName = "Id", GraphQlName = "Id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
            new ColumnDto { ColumnName = "Code", GraphQlName = "Code", DataType = "varchar", OrdinalPosition = 2 },
        };
        return new DbTable
        {
            DbName = "OrderStatus",
            GraphQlName = schema == "dbo" ? "OrderStatus" : $"{schema}_OrderStatus",
            NormalizedName = "orderstatus",
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.DbName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?> { [MetadataKeys.Enum.Table] = "Code" },
        };
    }

    private static string EnumBlock(string schemaText, string enumName)
    {
        var start = schemaText.IndexOf($"enum {enumName} {{", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"enum type {enumName} must be emitted");
        var end = schemaText.IndexOf('}', start);
        return schemaText[start..end];
    }

    [Fact]
    public void SchemaText_EnumTablesSharingNameAcrossSchemas_EachEmitsItsOwnEnumType()
    {
        var tables = new[] { EnumTable("dbo"), EnumTable("sales") };
        var model = new DbModel
        {
            Tables = tables,
            Metadata = new Dictionary<string, object?>(),
            EnumColumns = EnumColumnMap.Build(
                new DbModel { Tables = tables, Metadata = new Dictionary<string, object?>() },
                new Dictionary<EnumTableKey, IReadOnlyList<EnumValueEntry>>
                {
                    [new EnumTableKey("dbo", "OrderStatus")] = EnumValueSanitizer.SanitizeAll(new[] { "open", "closed" }),
                    [new EnumTableKey("sales", "OrderStatus")] = EnumValueSanitizer.SanitizeAll(new[] { "paid", "shipped" }),
                },
                new Dictionary<EnumTableKey, string>
                {
                    [new EnumTableKey("dbo", "OrderStatus")] = "Code",
                    [new EnumTableKey("sales", "OrderStatus")] = "Code",
                }),
        };

        var schemaText = SchemaGenerator.SchemaTextFromModel(model);

        var dbo = EnumBlock(schemaText, "OrderStatusValues");
        dbo.Should().Contain("OPEN").And.Contain("CLOSED").And.NotContain("PAID");
        var sales = EnumBlock(schemaText, "sales_OrderStatusValues");
        sales.Should().Contain("PAID").And.Contain("SHIPPED").And.NotContain("OPEN");
    }
}
