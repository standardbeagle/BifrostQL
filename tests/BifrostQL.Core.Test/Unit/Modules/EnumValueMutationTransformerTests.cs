using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Tests for <see cref="EnumValueMutationTransformer"/> — rewrites enum-named
/// input values to their stored DB values on insert/update, erroring on unknown
/// names. Model + map mirror <c>EnumColumnMapTests</c>.
/// </summary>
public class EnumValueMutationTransformerTests
{
    private const string EnumTable = "OrderStatus";
    private const string ValueColumn = "Code";

    private static DbModel BuildModelWithEnums()
    {
        var model = (DbModel)DbModelTestFixture.Create()
            .WithTable("Customers", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .WithTable(EnumTable, t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, ValueColumn)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("CustomerId", "int")
                .WithColumn("StatusCode", "varchar")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_StatusCode", "Orders", "StatusCode", EnumTable, ValueColumn)
            .WithForeignKey("FK_Orders_Customer", "Orders", "CustomerId", "Customers", "Id")
            .Build();

        var entries = EnumValueSanitizer.SanitizeAll(new[] { "active", "pending", "on hold" });
        var enumValues = new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = entries,
        };
        var resolvedValueColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnumTable] = ValueColumn,
        };
        model.EnumColumns = EnumColumnMap.Build(model, enumValues, resolvedValueColumns);
        return model;
    }

    private static IDbModel BuildModelWithoutEnums()
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("StatusCode", "varchar"))
            .Build();
    }

    private static MutationTransformContext Context(IDbModel model) =>
        new()
        {
            Model = model,
            UserContext = new Dictionary<string, object?>(),
        };

    private static string StatusGraphQlName(IDbModel model) =>
        model.GetTableFromDbName("Orders").Columns
            .First(c => string.Equals(c.ColumnName, "StatusCode", StringComparison.OrdinalIgnoreCase))
            .GraphQlName;

    [Fact]
    public void Transform_RewritesEnumNameToDbValue()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithEnums();
        var statusField = StatusGraphQlName(model);
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [statusField] = "ACTIVE",
        };

        var result = transformer.Transform(
            model.GetTableFromDbName("Orders"), MutationType.Insert, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data[statusField].Should().Be("active");
    }

    [Fact]
    public void Transform_UnknownName_ProducesErrorAndDoesNotRewrite()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithEnums();
        var statusField = StatusGraphQlName(model);
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [statusField] = "BOGUS",
        };

        var result = transformer.Transform(
            model.GetTableFromDbName("Orders"), MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle();
        result.Data[statusField].Should().Be("BOGUS");
    }

    [Fact]
    public void Transform_Delete_RewritesEnumNameToDbValue()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithEnums();
        var statusField = StatusGraphQlName(model);
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [statusField] = "ACTIVE",
        };

        var result = transformer.Transform(
            model.GetTableFromDbName("Orders"), MutationType.Delete, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data[statusField].Should().Be("active");
    }

    [Fact]
    public void AppliesTo_ReturnsTrue_OnDelete_WhenTableHasEnumColumns()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithEnums();

        var applies = transformer.AppliesTo(
            model.GetTableFromDbName("Orders"), MutationType.Delete, Context(model));

        applies.Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_ReturnsFalse_WhenModelHasNoEnumColumnsForTable()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithoutEnums();

        var applies = transformer.AppliesTo(
            model.GetTableFromDbName("Orders"), MutationType.Insert, Context(model));

        applies.Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_ReturnsTrue_WhenTableHasEnumColumns()
    {
        var transformer = new EnumValueMutationTransformer();
        var model = BuildModelWithEnums();

        var applies = transformer.AppliesTo(
            model.GetTableFromDbName("Orders"), MutationType.Insert, Context(model));

        applies.Should().BeTrue();
    }
}
