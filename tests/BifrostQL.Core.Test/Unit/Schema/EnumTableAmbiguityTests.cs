using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Enum-table metadata addresses its lookup table by BARE DbName. When two schemas both
/// define a table with that name, the pre-fix FirstOrDefault silently bound the GraphQL
/// enum type to whichever table enumerated first — the wrong schema's rows and filter
/// surface. Schema generation must fail fast instead, the same rule as
/// <see cref="DbModel.GetTableFromDbName(string)"/>.
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
            Metadata = new Dictionary<string, object?> { [EnumTableConfig.MetadataKey] = "Code" },
        };
    }

    [Fact]
    public void SchemaText_DuplicateEnumTableNameAcrossSchemas_FailsFastInsteadOfGuessing()
    {
        var tables = new[] { EnumTable("dbo"), EnumTable("sales") };
        var entries = EnumValueSanitizer.SanitizeAll(new[] { "open", "closed" });
        var model = new DbModel
        {
            Tables = tables,
            Metadata = new Dictionary<string, object?>(),
            EnumColumns = EnumColumnMap.Build(
                new DbModel { Tables = tables, Metadata = new Dictionary<string, object?>() },
                new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OrderStatus"] = entries,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OrderStatus"] = "Code",
                }),
        };

        var act = () => SchemaGenerator.SchemaTextFromModel(model);

        act.Should().Throw<BifrostExecutionError>().WithMessage("*ambiguous*",
            "binding the enum type to an arbitrary schema's table silently mis-targets rows and filters");
    }
}
