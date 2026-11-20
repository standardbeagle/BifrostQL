using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Schema;

/// <summary>
/// <c>populate: host</c> marks a column the host's own mutation transformer stamps. A
/// <c>NOT NULL</c> column so marked is optional in the insert input (the schema keeps its
/// constraint instead of being weakened to suit the generator), the config validator accepts
/// the value, the form hint agrees it is not client-required, and the audit module leaves the
/// column alone. The control column is the same shape without the marker and stays required.
/// </summary>
public sealed class HostPopulatedColumnSchemaTests
{
    private static IDbModel Model() => DbModelTestFixture.Create()
        .WithTable("time_entries", t => t
            .WithSchema("public")
            .WithPrimaryKey("id")
            .WithColumn("person_id", "int", isNullable: false)
            .WithColumn("minutes", "int", isNullable: false)
            .WithColumnMetadata("person_id", MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.Host))
        .Build();

    private static IDbTable Table(IDbModel model) => model.Tables.Single(t => t.DbName == "time_entries");

    private static string Field(string sdl, string column) =>
        sdl.Split('\n').Single(l => l.TrimStart().StartsWith($"{column} :")).Trim();

    [Fact]
    public void HostPopulatedNotNullColumn_IsOptionalInTheInsertInput()
    {
        var sdl = new TableSchemaGenerator(Table(Model())).GetMutationParameterType(MutateActions.Insert, IdentityType.None);

        Field(sdl, "person_id").Should().Be("person_id : Int", "the host's transformer stamps it; sdl:\n{0}", sdl);
        Field(sdl, "minutes").Should().Be("minutes : Int!", "the same shape without the marker stays required");
    }

    [Fact]
    public void HostPopulatedValue_IsAcceptedByTheConfigValidator()
    {
        var act = () => ModelConfigValidator.Validate(Model());

        act.Should().NotThrow();
    }

    [Fact]
    public void HostPopulatedColumn_IsNotAClientRequiredFormField()
    {
        var table = Table(Model());

        ValidationRules.ForColumn(table.ColumnLookup["person_id"]).Required.Should().BeFalse();
        ValidationRules.ForColumn(table.ColumnLookup["minutes"]).Required.Should().BeTrue();
    }

    [Fact]
    public void HostPopulatedColumn_IsLeftAloneByTheAuditModule()
    {
        var model = Model();

        new AuditMutationTransformer().AppliesTo(
                Table(model), MutationType.Insert,
                new MutationTransformContext { Model = model, UserContext = new Dictionary<string, object?>() })
            .Should().BeFalse("only audit populators bring the audit module in");
    }
}
