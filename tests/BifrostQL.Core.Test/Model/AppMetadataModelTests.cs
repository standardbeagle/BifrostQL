using System.Text.Json;
using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for the app-metadata overlay model
/// (<see cref="AppMetadataModel"/>, <see cref="EntityMetadata"/>,
/// <see cref="FieldMetadata"/>) and its JSON contract
/// (<see cref="AppMetadataJson"/>).
///
/// The overlay is a NEW layer on top of the existing schema-metadata system;
/// these tests assert it is pure data and round-trips losslessly through a
/// stable camelCase JSON contract.
/// </summary>
public class AppMetadataModelTests
{
    private static AppMetadataModel BuildSample()
    {
        return new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.users"] = new EntityMetadata
                {
                    Label = "Users",
                    Icon = "person",
                    DisplayFields = new[] { "first_name", "last_name" },
                    NavPlacement = "admin",
                    Fields = new Dictionary<string, FieldMetadata>
                    {
                        ["email"] = new FieldMetadata
                        {
                            Widget = "email",
                            Validation = "required",
                            Visible = true,
                            ReadOnly = false,
                            HelpText = "Primary contact email",
                            Group = "contact",
                        },
                        ["internal_id"] = new FieldMetadata
                        {
                            Visible = false,
                            ReadOnly = true,
                        },
                    },
                },
                ["sales.orders"] = new EntityMetadata
                {
                    Label = "Orders",
                },
            },
        };
    }

    [Fact]
    public void AppMetadata_RoundTripsThroughJson_Losslessly()
    {
        var original = BuildSample();

        var json = AppMetadataJson.Serialize(original);
        var restored = AppMetadataJson.Deserialize(json);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        var metadata = BuildSample();

        var json = AppMetadataJson.Serialize(metadata);

        json.Should().Contain("\"entities\"");
        json.Should().Contain("\"displayFields\"");
        json.Should().Contain("\"navPlacement\"");
        json.Should().Contain("\"helpText\"");
        json.Should().Contain("\"readOnly\"");
        // .NET PascalCase property names must not leak into the contract.
        json.Should().NotContain("\"Entities\"");
        json.Should().NotContain("\"DisplayFields\"");
    }

    [Fact]
    public void Serialize_PreservesQualifiedTableNameKeysVerbatim()
    {
        var metadata = BuildSample();

        var json = AppMetadataJson.Serialize(metadata);

        // Dictionary keys are qualified table / field names — not camelCased.
        json.Should().Contain("\"dbo.users\"");
        json.Should().Contain("\"sales.orders\"");
        json.Should().Contain("\"internal_id\"");
    }

    [Fact]
    public void Serialize_OmitsNullOptionalValues()
    {
        var metadata = new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.t"] = new EntityMetadata { Label = "T" },
            },
        };

        var json = AppMetadataJson.Serialize(metadata);

        json.Should().NotContain("\"icon\"");
        json.Should().NotContain("\"navPlacement\"");
    }

    [Fact]
    public void Deserialize_AbsentBooleans_UseModelDefaults()
    {
        // visible defaults true, readOnly defaults false when absent from JSON.
        const string json =
            "{\"entities\":{\"dbo.t\":{\"fields\":{\"f\":{\"widget\":\"text\"}}}}}";

        var restored = AppMetadataJson.Deserialize(json);

        var field = restored.Entities["dbo.t"].Fields["f"];
        field.Visible.Should().BeTrue();
        field.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Empty_AppMetadata_RoundTrips()
    {
        var original = new AppMetadataModel();

        var json = AppMetadataJson.Serialize(original);
        var restored = AppMetadataJson.Deserialize(json);

        restored.Entities.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_NullJson_Throws()
    {
        var act = () => AppMetadataJson.Deserialize("null");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Records_WithEqualContent_AreEqual()
    {
        // Pure-data value semantics: equal content compares equal.
        var a = new FieldMetadata { Widget = "text", Group = "g" };
        var b = new FieldMetadata { Widget = "text", Group = "g" };

        a.Should().Be(b);
    }
}
