using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Tests that <see cref="SchemaGenerator.SchemaTextFromModel"/> renders an enum
/// column's FIELD type as the enum (<c>{Table}Values</c>) and its FILTER argument
/// type as <c>FilterType{Table}ValuesInput</c> — driven by <c>model.EnumColumns</c>.
/// Mirrors the orders/status enum scenario from
/// <see cref="BifrostQL.Core.Test.Schema.EnumSchemaEmissionTests"/>.
/// </summary>
public class EnumColumnRenderingTests
{
    private const string EnumTable = "status";
    private const string ValueColumn = "code";

    /// <summary>
    /// status is the lookup/enum table (value column = code, GraphQL enum = statusValues).
    /// Orders.status has an FK to status.code (value column) → enum by FK.
    /// </summary>
    private static IDbModel BuildModel()
    {
        return DbModelTestFixture.Create()
            .WithTable(EnumTable, t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, ValueColumn)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("code", "varchar")
                .WithColumn("label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("status", "varchar")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Status", "Orders", "status", EnumTable, ValueColumn)
            .Build();
    }

    private static EnumColumnMap BuildMap(IDbModel model)
    {
        var entries = EnumValueSanitizer.SanitizeAll(new[] { "active", "inactive" });
        var enumValues = new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = entries,
        };
        var resolvedValueColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = ValueColumn,
        };
        return EnumColumnMap.Build(model, enumValues, resolvedValueColumns);
    }

    [Fact]
    public void SchemaTextFromModel_EnumColumn_RendersEnumFieldType()
    {
        // Arrange
        var model = (DbModel)BuildModel();
        model.EnumColumns = BuildMap(model);

        // Act
        var text = SchemaGenerator.SchemaTextFromModel(model);

        // Assert — the orders status column field renders as the enum type
        // (a "!" suffix is allowed by nullability, so match the prefix only).
        text.Should().Contain("status : statusValues");
    }

    [Fact]
    public void SchemaTextFromModel_EnumColumn_RendersEnumFilterInputType()
    {
        // Arrange
        var model = (DbModel)BuildModel();
        model.EnumColumns = BuildMap(model);

        // Act
        var text = SchemaGenerator.SchemaTextFromModel(model);

        // Assert — the orders filter input renders the enum filter input type
        text.Should().Contain("status : FilterTypestatusValuesInput");
    }

    [Fact]
    public void SchemaTextFromModel_NonEnumColumn_StillRendersScalar()
    {
        // Arrange
        var model = (DbModel)BuildModel();
        model.EnumColumns = BuildMap(model);

        // Act
        var text = SchemaGenerator.SchemaTextFromModel(model);

        // Assert — a non-enum column (Id) keeps its scalar type, never the enum.
        text.Should().Contain("Id : Int");
        text.Should().NotContain("Id : statusValues");
    }
}
