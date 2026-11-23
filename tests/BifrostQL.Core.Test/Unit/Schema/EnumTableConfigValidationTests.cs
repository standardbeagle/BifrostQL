using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// A lookup table's <c>enum</c> value column is checked at model load, by
/// <see cref="ModelConfigValidator"/> calling <c>EnumTableConfig.ResolveValueColumn</c> itself.
/// A typo used to pass validation and throw a plain <see cref="InvalidOperationException"/> later,
/// at schema build, so the CLI lost its pointer to <c>bifrost config-validate</c> (it reacts only
/// to <see cref="ModelConfigValidationException"/>).
/// </summary>
public sealed class EnumTableConfigValidationTests
{
    private static IDbModel StatusTable(string enumValue)
        => DbModelTestFixture.Create()
            .WithTable("status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("code", "varchar")
                .WithColumn("label", "varchar")
                .WithMetadata(MetadataKeys.Enum.Table, enumValue))
            .Build();

    [Fact]
    public void Validate_EnumValueColumnTypo_IsAConfigValidationError()
    {
        var model = StatusTable("cod");

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<ModelConfigValidationException>()
            .Which.Message.Should().Contain("dbo.status [enum]: 'cod'")
            .And.Contain("Enum value column 'cod' configured on table 'status' does not exist.");
    }

    [Fact]
    public void Validate_EnumValueAndLabelColumnTypo_NamesTheValueColumn()
    {
        var model = StatusTable("cod:label");

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<ModelConfigValidationException>()
            .Which.Message.Should().Contain("Enum value column 'cod'");
    }

    [Theory]
    [InlineData("code")]
    [InlineData("code:label")]
    [InlineData("true")]
    public void Validate_EnumWithAnExistingOrAutoDetectedValueColumn_Passes(string enumValue)
    {
        var model = StatusTable(enumValue);

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EnumTrueWithNoStringColumn_Passes()
    {
        // Auto-detection finding nothing is the legitimate best-effort case: the table stays a
        // plain table, as the schema build has always treated it.
        var model = DbModelTestFixture.Create()
            .WithTable("status", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("id")
                .WithColumn("rank", "int")
                .WithMetadata(MetadataKeys.Enum.Table, "true"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
