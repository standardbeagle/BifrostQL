using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public sealed class ExtendedServerValidationTransformerTests
{
    [Fact]
    public void Transform_EnforcesConfiguredMetadataRules()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        var transformer = new ExtendedServerValidationTransformer();

        var result = transformer.Transform(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Email"] = "not-an-email", ["Age"] = 17 },
            NewContext(model));

        result.Errors.Should().Contain("Name is required.");
        result.Errors.Should().Contain("Email must be a valid email.");
        result.Errors.Should().Contain("Age must be at least 18.");
    }

    [Fact]
    public void Transform_RunsRegisteredPluginValidator()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "custom";
        var transformer = new ExtendedServerValidationTransformer(new[] { new CustomValidator() });

        var result = transformer.Transform(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Age"] = 30 },
            NewContext(model));

        result.Errors.Should().Contain("custom validation failed");
    }

    [Fact]
    public void Transform_ReportsMissingPluginValidator()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "missing-provider";
        var transformer = new ExtendedServerValidationTransformer();

        var result = transformer.Transform(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Age"] = 30 },
            NewContext(model));

        result.Errors.Should().Contain("Server validation provider 'missing-provider' is not registered.");
    }

    private static MutationTransformContext NewContext(IDbModel model)
        => new()
        {
            Model = model,
            UserContext = new Dictionary<string, object?>(),
        };

    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Contacts", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "varchar")
                .WithColumn("Email", "varchar")
                .WithColumn("Age", "int")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Name", MetadataKeys.Validation.Required, "true")
                .WithColumnMetadata("Email", MetadataKeys.Validation.Pattern, @"^[^@]+@[^@]+\.[^@]+$")
                .WithColumnMetadata("Email", MetadataKeys.Validation.PatternMessage, "Email must be a valid email.")
                .WithColumnMetadata("Age", MetadataKeys.Validation.Min, "18"))
            .Build();

    private sealed class CustomValidator : IServerValidationProvider
    {
        public string Name => "custom";

        public IReadOnlyList<string> Validate(ServerValidationContext context)
            => new[] { "custom validation failed" };
    }
}
