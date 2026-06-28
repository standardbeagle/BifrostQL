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
    public async Task Length_EnforcesMinAndMax()
    {
        var model = LengthModel();
        var table = model.GetTableFromDbName("T");

        var tooShort = await Run(table, model, new() { ["Code"] = "ab" });
        tooShort.Should().Contain("Code must be at least 3 characters.");

        var tooLong = await Run(table, model, new() { ["Code"] = "abcdefghij" });
        tooLong.Should().Contain("Code must be at most 5 characters.");

        var ok = await Run(table, model, new() { ["Code"] = "abcd" });
        ok.Should().BeEmpty();
    }

    [Fact]
    public async Task Range_EnforcesMax()
    {
        var model = RangeModel();
        var table = model.GetTableFromDbName("T");

        (await Run(table, model, new() { ["Score"] = 150 }))
            .Should().Contain("Score must be at most 100.");

        (await Run(table, model, new() { ["Score"] = 50 })).Should().BeEmpty();
    }

    [Fact]
    public async Task Range_IgnoresNonNumericValues()
    {
        var model = RangeModel();
        var table = model.GetTableFromDbName("T");

        // A non-numeric value cannot be range-checked; it must not throw and must
        // not produce a spurious range error.
        (await Run(table, model, new() { ["Score"] = "not-a-number" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Required_NotEnforcedOnUpdateWhenFieldAbsent()
    {
        var model = RequiredModel();
        var table = model.GetTableFromDbName("T");

        // Partial update omitting the required field is allowed.
        (await RunUpdate(table, model, new() { ["Other"] = "x" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Required_EnforcedOnUpdateWhenFieldPresentButBlank()
    {
        var model = RequiredModel();
        var table = model.GetTableFromDbName("T");

        (await RunUpdate(table, model, new() { ["Name"] = "  " }))
            .Should().Contain("Name is required.");
    }

    [Fact]
    public async Task AppliesByDefault_WithoutAnyFlag()
    {
        // Validation is on by default: a declared rule is enforced without any
        // server-validation enablement metadata.
        var model = DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true"))
            .Build();
        var table = model.GetTableFromDbName("T");

        Transformer.AppliesTo(table, MutationType.Insert, NewContext(model)).Should().BeTrue();
        (await Run(table, model, new() { ["Name"] = "" })).Should().Contain("Name is required.");
    }

    [Fact]
    public async Task Disabled_AtTableLevel_TurnsValidationOff()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithMetadata(MetadataKeys.Validation.Server, "off")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true"))
            .Build();
        var table = model.GetTableFromDbName("T");

        Transformer.AppliesTo(table, MutationType.Insert, NewContext(model)).Should().BeFalse();
        // Even if invoked directly, a disabled table produces no errors.
        (await Run(table, model, new() { ["Name"] = "" })).Should().BeEmpty();
    }

    [Fact]
    public async Task ValidatesAllColumnsByDefault_ColumnCanOptOut()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Note", "varchar")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true")
                .WithColumnMetadata("Note", MetadataKeys.Validation.Required, "true")
                // Note opts out individually; Name still validates.
                .WithColumnMetadata("Note", MetadataKeys.Validation.Server, "off"))
            .Build();
        var table = model.GetTableFromDbName("T");

        var errors = await Run(table, model, new() { ["Name"] = null, ["Note"] = null });
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

    [Fact]
    public async Task Pattern_IsAnchoredLikeHtml5()
    {
        var model = PatternModel("[0-9]{3}");
        var table = model.GetTableFromDbName("T");

        // Unanchored "[0-9]{3}" matches "abc123def" as a substring; anchoring makes
        // it a full-string match, so the server rejects what the client/HTML reject.
        (await Run(table, model, new() { ["Code"] = "abc123def" }))
            .Should().Contain("Code is invalid.");

        (await Run(table, model, new() { ["Code"] = "123" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Pattern_AlreadyAnchored_IsNotDoubleWrapped()
    {
        var model = PatternModel("^[0-9]{3}$");
        var table = model.GetTableFromDbName("T");

        (await Run(table, model, new() { ["Code"] = "123" })).Should().BeEmpty();
        (await Run(table, model, new() { ["Code"] = "12" })).Should().Contain("Code is invalid.");
    }

    [Fact]
    public async Task Pattern_Invalid_ReportsCleanErrorInsteadOfThrowing()
    {
        var model = PatternModel("(");
        var table = model.GetTableFromDbName("T");

        (await Run(table, model, new() { ["Code"] = "x" }))
            .Should().Contain("Code has an invalid validation pattern.");
    }

    [Fact]
    public async Task Pattern_Catastrophic_TimesOutInsteadOfHanging()
    {
        // A ReDoS pattern + adversarial input must not hang the mutation; the
        // bounded match time surfaces a clean error.
        var model = PatternModel("(a+)+$");
        var table = model.GetTableFromDbName("T");

        var input = new string('a', 50) + "!";
        (await Run(table, model, new() { ["Code"] = input }))
            .Should().Contain("Code could not be validated (pattern too complex).");
    }

    [Fact]
    public async Task InputType_Email_IsValidated()
    {
        var model = InputTypeModel("email");
        var table = model.GetTableFromDbName("T");

        (await Run(table, model, new() { ["Code"] = "not-an-email" }))
            .Should().Contain("Code must be a valid email address.");
        (await Run(table, model, new() { ["Code"] = "a@b.com" })).Should().BeEmpty();
    }

    [Fact]
    public async Task InputType_Url_IsValidatedAndHttpOnly()
    {
        var model = InputTypeModel("url");
        var table = model.GetTableFromDbName("T");

        (await Run(table, model, new() { ["Code"] = "ftp://example.com" }))
            .Should().Contain("Code must be a valid URL.");
        (await Run(table, model, new() { ["Code"] = "https://example.com" })).Should().BeEmpty();
    }

    private static IDbModel PatternModel(string pattern)
        => DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Code", "varchar")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Code", MetadataKeys.Validation.Pattern, pattern))
            .Build();

    private static IDbModel InputTypeModel(string inputType)
        => DbModelTestFixture.Create()
            .WithTable("T", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Code", "varchar")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Code", MetadataKeys.Validation.InputType, inputType))
            .Build();

    private static async Task<IReadOnlyList<string>> Run(IDbTable table, IDbModel model, Dictionary<string, object?> data)
        => (await Transformer.TransformAsync(table, MutationType.Insert, data, NewContext(model))).Errors;

    private static async Task<IReadOnlyList<string>> RunUpdate(IDbTable table, IDbModel model, Dictionary<string, object?> data)
        => (await Transformer.TransformAsync(table, MutationType.Update, data, NewContext(model))).Errors;

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
