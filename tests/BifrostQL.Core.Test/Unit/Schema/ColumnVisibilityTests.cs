using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL.Types;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Slice 4: GraphQL schema generation honors per-column visibility metadata.
/// A column marked <c>visibility: hidden</c> (via a 3-part metadata rule such as
/// <c>*.companies.phone { visibility: hidden }</c>, or set directly on the column)
/// must disappear from the profile's emitted GraphQL surface — the object type,
/// the filter input, and the sort enum — while remaining in the underlying model
/// so joins / SQL / identity continue to work.
/// </summary>
public class ColumnVisibilityTests
{
    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string SchemaText(IDbModel model) =>
        (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, true })!;

    // Arrange: companies(company_id, name, phone) with no hide rule.
    private static IDbModel BuildVisibleModel() =>
        DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name")
                .WithColumn("phone"))
            .Build();

    // Arrange: same model but phone is hidden, mirroring
    // "*.companies.phone { visibility: hidden }" landing on ColumnDto.Metadata.
    private static IDbModel BuildHiddenPhoneModel() =>
        DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name")
                .WithColumn("phone")
                .WithColumnMetadata("phone", MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
            .Build();

    // Returns just the "type companies { ... }" block body so field assertions
    // are scoped to the object type and not other generated types.
    private static string TypeBlock(string sdl, string header)
    {
        var start = sdl.IndexOf(header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected '{header}' in SDL");
        var end = sdl.IndexOf('}', start);
        return sdl.Substring(start, end - start);
    }

    [Fact]
    public void Control_NoHideRule_PhoneFieldPresentOnType()
    {
        // Arrange
        var model = BuildVisibleModel();

        // Act
        var sdl = SchemaText(model);
        var companiesType = TypeBlock(sdl, "type companies {");

        // Assert
        companiesType.Should().Contain("company_id :");
        companiesType.Should().Contain("name :");
        companiesType.Should().Contain("phone :");
    }

    [Fact]
    public void HiddenColumn_OmittedFromObjectType()
    {
        // Arrange
        var model = BuildHiddenPhoneModel();

        // Act
        var sdl = SchemaText(model);
        var companiesType = TypeBlock(sdl, "type companies {");

        // Assert: company_id and name remain, phone is gone.
        companiesType.Should().Contain("company_id :");
        companiesType.Should().Contain("name :");
        companiesType.Should().NotContain("phone :");
    }

    [Fact]
    public void HiddenColumn_OmittedFromFilterInputType()
    {
        // Arrange
        var model = BuildHiddenPhoneModel();

        // Act
        var sdl = SchemaText(model);
        var filterType = TypeBlock(sdl, "input TableFiltercompaniesInput {");

        // Assert
        filterType.Should().Contain("company_id :");
        filterType.Should().Contain("name :");
        filterType.Should().NotContain("phone :");
    }

    [Fact]
    public void HiddenColumn_OmittedFromSortEnum()
    {
        // Arrange
        var model = BuildHiddenPhoneModel();

        // Act
        var sdl = SchemaText(model);
        var sortEnum = TypeBlock(sdl, "enum companiesSortEnum {");

        // Assert
        sortEnum.Should().Contain("company_id_asc");
        sortEnum.Should().Contain("name_asc");
        sortEnum.Should().NotContain("phone_asc");
        sortEnum.Should().NotContain("phone_desc");
    }

    [Fact]
    public void Control_NoHideRule_PhonePresentInFilterAndSort()
    {
        // Arrange
        var model = BuildVisibleModel();

        // Act
        var sdl = SchemaText(model);

        // Assert
        TypeBlock(sdl, "input TableFiltercompaniesInput {").Should().Contain("phone :");
        TypeBlock(sdl, "enum companiesSortEnum {").Should().Contain("phone_asc");
    }

    [Fact]
    public void HiddenColumn_SchemaStillBuilds_AndOmitsField()
    {
        // Arrange
        var model = BuildHiddenPhoneModel();

        // Act: the full GraphQL.NET schema must initialize without error.
        var schema = DbSchema.FromModel(model);
        schema.Initialize();

        // Assert: the companies object type exposes company_id and name, not phone.
        var companies = schema.AllTypes.OfType<IObjectGraphType>().FirstOrDefault(t => t.Name == "companies");
        companies.Should().NotBeNull();
        var fieldNames = companies!.Fields.Select(f => f.Name).ToArray();
        fieldNames.Should().Contain("company_id");
        fieldNames.Should().Contain("name");
        fieldNames.Should().NotContain("phone");
    }

    [Fact]
    public void HiddenColumn_RemainsInUnderlyingModel()
    {
        // Arrange / Act: hiding is a schema-emission concern only — the column
        // must stay in the model so joins / SQL / identity keep working.
        var model = BuildHiddenPhoneModel();
        var companies = model.GetTableFromDbName("companies");

        // Assert
        companies.Columns.Should().Contain(c => c.DbName == "phone");
    }
}
