using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

public class DbSchemaProfileOverloadTests
{
    // Arrange: a minimal single-table model.
    private static BifrostQL.Core.Model.IDbModel BuildCompaniesModel() =>
        DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name"))
            .Build();

    [Fact]
    public void FromModel_NoProfileOverload_ContainsCompaniesType()
    {
        // Arrange
        var model = BuildCompaniesModel();

        // Act
        var schema = DbSchema.FromModel(model);
        schema.Initialize();

        // Assert
        schema.AllTypes.Any(t => t.Name == "companies").Should().BeTrue();
    }

    [Fact]
    public void FromModel_ProfileOverload_BehavesIdenticallyThisSlice()
    {
        // Arrange
        var model = BuildCompaniesModel();

        // Act
        var withNull = DbSchema.FromModel(model);
        withNull.Initialize();
        var withProfile = DbSchema.FromModel(model, new BifrostProfile { Name = "dev" });
        withProfile.Initialize();

        // Assert: behavioral parity — both expose the companies type.
        withNull.AllTypes.Any(t => t.Name == "companies").Should().BeTrue();
        withProfile.AllTypes.Any(t => t.Name == "companies").Should().BeTrue();
    }
}
