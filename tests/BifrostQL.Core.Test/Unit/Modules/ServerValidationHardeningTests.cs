using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Coverage for server-validation rules and mutation semantics that the
/// original suite did not exercise: length bounds, numeric max, partial-update
/// required handling, column-level enablement, and the column-vs-graphql key
/// lookup.
/// </summary>
public sealed class ServerValidationHardeningTests
{
    private static readonly ExtendedServerValidationTransformer Transformer = new();

    [Fact]
    public void Length_EnforcesMinAndMax()
    {
        var model = LengthModel();
        var table = model.GetTableFromDbName("T");

        var tooShort = Run(table, model, new() { ["Code"] = "ab" });
        tooShort.Should().Contain("Code must be at least 3 characters.");

        var tooLong = Run(table, model, new() { ["Code"] = "abcdefghij" });
        tooLong.Should().Contain("Code must be at most 5 characters.");

        var ok = Run(table, model, new() { ["Code"] = "abcd" });
        ok.Should().BeEmpty();
    }

    [Fact]
    public void Range_EnforcesMax()
    {
        var model = RangeModel();
        var table = model.GetTableFromDbName("T");

        Run(table, model, new() { ["Score"] = 150 })
            .Should().Contain("Score must be at most 100.");

        Run(table, model, new() { ["Score"] = 50 }).Should().BeEmpty();
    }

    [Fact]
    public void Range_IgnoresNonNumericValues()
    {
        var model = RangeModel();
        var table = model.GetTableFromDbName("T");

        // A non-numeric value cannot be range-checked; it must not throw and must
        // not produce a spurious range error.
        Run(table, model, new() { ["Score"] = "not-a-number" }).Should().BeEmpty();
    }

    [Fact]
    public void Required_NotEnforcedOnUpdateWhenFieldAbsent()
    {
        var model = RequiredModel();
        var table = model.GetTableFromDbName("T");

        // Partial update omitting the required field is allowed.
        RunUpdate(table, model, new() { ["Other"] = "x" }).Should().BeEmpty();
    }

    [Fact]
    public void Required_EnforcedOnUpdateWhenFieldPresentButBlank()
    {
        var model = RequiredModel();
        var table = model.GetTableFromDbName("T");

        RunUpdate(table, model, new() { ["Name"] = "  " })
            .Should().Contain("Name is required.");
    }

    [Fact]
    public void Disabled_WhenNoTableOrColumnFlag_DoesNotApply()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true"))
            .Build();
        var table = model.GetTableFromDbName("T");

        // Required flag alone, without server-validation enablement, must not run.
        Transformer.AppliesTo(table, MutationType.Insert, NewContext(model)).Should().BeFalse();
    }

    [Fact]
    public void ColumnLevelEnablement_ValidatesOnlyFlaggedColumn()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Note", "varchar")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Server, "true")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true")
                .WithColumnMetadata("Note", MetadataKeys.Validation.Required, "true"))
            .Build();
        var table = model.GetTableFromDbName("T");

        Transformer.AppliesTo(table, MutationType.Insert, NewContext(model)).Should().BeTrue();

        // Note is required but not server-enabled → no error; Name is.
        var errors = Run(table, model, new() { ["Note"] = null });
        errors.Should().Contain("Name is required.");
        errors.Should().NotContain("Note is required.");
    }

    [Fact]
    public void DoesNotApplyToDeleteMutations()
    {
        var model = RequiredModel();
        var table = model.GetTableFromDbName("T");

        Transformer.AppliesTo(table, MutationType.Delete, NewContext(model)).Should().BeFalse();
    }

    private static IReadOnlyList<string> Run(IDbTable table, IDbModel model, Dictionary<string, object?> data)
        => Transformer.Transform(table, MutationType.Insert, data, NewContext(model)).Errors;

    private static IReadOnlyList<string> RunUpdate(IDbTable table, IDbModel model, Dictionary<string, object?> data)
        => Transformer.Transform(table, MutationType.Update, data, NewContext(model)).Errors;

    private static MutationTransformContext NewContext(IDbModel model)
        => new() { Model = model, UserContext = new Dictionary<string, object?>() };

    private static IDbModel LengthModel()
        => DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Code", "varchar")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Code", MetadataKeys.Validation.MinLength, "3")
                .WithColumnMetadata("Code", MetadataKeys.Validation.MaxLength, "5"))
            .Build();

    private static IDbModel RangeModel()
        => DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Score", "int")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Score", MetadataKeys.Validation.Max, "100"))
            .Build();

    private static IDbModel RequiredModel()
        => DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Other", "varchar")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true"))
            .Build();
}
