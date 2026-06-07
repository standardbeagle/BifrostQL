using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Tests that <see cref="SchemaGenerator.SchemaTextFromModel"/> emits, for each
/// lookup-table enum carried on <c>model.EnumColumns</c>, a GraphQL
/// <c>enum {Table}Values</c> type and its <c>input FilterType{Table}ValuesInput</c>
/// filter type. Mirrors the orders/status enum scenario from
/// <see cref="BifrostQL.Core.Test.Schema.EnumColumnMapTests"/>.
/// </summary>
public class EnumSchemaEmissionTests
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
    public void SchemaTextFromModel_EmitsEnumAndFilterInputTypes()
    {
        // Arrange — the FK-based fixture builds a concrete DbModel, which exposes the setter.
        var model = (DbModel)BuildModel();
        model.EnumColumns = BuildMap(model);

        // Act
        var text = SchemaGenerator.SchemaTextFromModel(model);

        // Assert
        text.Should().Contain("enum statusValues {");
        text.Should().Contain("ACTIVE");
        text.Should().Contain("input FilterTypestatusValuesInput {");
    }

    [Fact]
    public void SchemaTextFromModel_NoEnumColumns_OmitsEnumTypes()
    {
        // Arrange — same model, but no EnumColumns attached.
        var model = BuildModel();

        // Act
        var text = SchemaGenerator.SchemaTextFromModel(model);

        // Assert — emission must be a complete no-op.
        text.Should().NotContain("enum statusValues {");
        text.Should().NotContain("input FilterTypestatusValuesInput {");
    }
}
