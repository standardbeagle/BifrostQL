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
    public async Task Transform_EnforcesConfiguredMetadataRules()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Email"] = "not-an-email", ["Age"] = 17 },
            NewContext(model));

        result.Errors.Should().Contain("Name is required.");
        result.Errors.Should().Contain("Email must be a valid email.");
        result.Errors.Should().Contain("Age must be at least 18.");
    }

    [Fact]
    public async Task Transform_RunsRegisteredPluginValidator()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "custom";
        var transformer = new ExtendedServerValidationTransformer(new[] { new CustomValidator() });

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Age"] = 30 },
            NewContext(model));

        result.Errors.Should().Contain("custom validation failed");
    }

    [Fact]
    public async Task Transform_RunsAsyncPluginValidator_ErrorAbortsMutation()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "async-unique";
        var transformer = new ExtendedServerValidationTransformer(new[] { new AsyncUniquenessValidator() });

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "taken@example.com", ["Age"] = 30 },
            NewContext(model));

        // The async provider's simulated lookup found a duplicate; its error surfaces
        // in the result, which the resolver turns into an aborted mutation.
        result.Errors.Should().Contain("Email taken@example.com is already in use.");
    }

    [Fact]
    public async Task Transform_SyncProviderStillRunsViaDefaultAsyncBridge()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "custom";
        // CustomValidator only overrides the sync Validate; the default ValidateAsync
        // bridge must still surface its error through the now-async pipeline.
        var transformer = new ExtendedServerValidationTransformer(new[] { new CustomValidator() });

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Age"] = 30 },
            NewContext(model));

        result.Errors.Should().Contain("custom validation failed");
    }

    [Fact]
    public async Task Transform_ReportsMissingPluginValidator()
    {
        var model = BuildModel();
        var table = model.GetTableFromDbName("Contacts");
        table.Metadata[MetadataKeys.Validation.Plugin] = "missing-provider";
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Age"] = 30 },
            NewContext(model));

        result.Errors.Should().Contain("Server validation provider 'missing-provider' is not registered.");
    }

    [Fact]
    public async Task Transform_ValueOffStepGrid_ProducesError()
    {
        var model = BuildStepModel();
        var table = model.GetTableFromDbName("Products");
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Price"] = 1.30m },
            NewContext(model));

        result.Errors.Should().Contain("Price must be in increments of 0.25.");
    }

    [Fact]
    public async Task Transform_ValueOnStepGrid_NoStepError()
    {
        var model = BuildStepModel();
        var table = model.GetTableFromDbName("Products");
        var transformer = new ExtendedServerValidationTransformer();

        var result = await transformer.TransformAsync(
            table,
            MutationType.Insert,
            new Dictionary<string, object?> { ["Price"] = 1.25m },
            NewContext(model));

        result.Errors.Should().NotContain(e => e.Contains("increments"));
    }

    [Fact]
    public async Task Transform_StepGridOriginIsMin_WhenMinPresent()
    {
        // origin = min (1), step 0.5: 2.0 is on-grid (2 steps), 2.25 is off-grid.
        var model = DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Price", "decimal")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Price", MetadataKeys.Validation.Min, "1")
                .WithColumnMetadata("Price", MetadataKeys.Validation.Step, "0.5"))
            .Build();
        var table = model.GetTableFromDbName("Products");
        var transformer = new ExtendedServerValidationTransformer();

        var onGrid = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["Price"] = 2.0m }, NewContext(model));
        var offGrid = await transformer.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?> { ["Price"] = 2.25m }, NewContext(model));

        onGrid.Errors.Should().NotContain(e => e.Contains("increments"));
        offGrid.Errors.Should().Contain("Price must be in increments of 0.5.");
    }

    private static IDbModel BuildStepModel()
        => DbModelTestFixture.Create()
            .WithTable("Products", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Price", "decimal")
                .WithMetadata(MetadataKeys.Validation.Server, "enabled")
                .WithColumnMetadata("Price", MetadataKeys.Validation.Step, "0.25"))
            .Build();

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

    // Overrides ValidateAsync to do a simulated async lookup (e.g. a uniqueness query).
    private sealed class AsyncUniquenessValidator : IServerValidationProvider
    {
        public string Name => "async-unique";

        // Async-only provider: sync entry returns no errors; real work is in ValidateAsync.
        public IReadOnlyList<string> Validate(ServerValidationContext context)
            => Array.Empty<string>();

        public async ValueTask<IReadOnlyList<string>> ValidateAsync(
            ServerValidationContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield(); // stand in for an awaited DB/service lookup
            var email = context.Data.TryGetValue("Email", out var v) ? v as string : null;
            return email == "taken@example.com"
                ? new[] { $"Email {email} is already in use." }
                : Array.Empty<string>();
        }
    }
}
