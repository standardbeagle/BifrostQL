using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Verifies the bundled per-connection profile config (&lt;schema&gt;.bifrost.json)
/// is embedded and parses into the expected profile shapes.
/// </summary>
public class SampleConfigTests
{
    [Fact]
    public void LoadSampleConfig_Crm_ParsesShowcaseProfile()
    {
        // Act
        var json = QuickstartSchemas.LoadSampleConfig("crm");

        // Assert: the embedded resource exists and deserializes to a single
        // config-driven showcase profile (the dropdown prepends the synthesized
        // raw default, so the user sees exactly two options).
        json.Should().NotBeNull();

        var doc = JsonDocument.Parse(json!);
        var profiles = doc.RootElement.GetProperty("profiles");
        profiles.GetArrayLength().Should().Be(1);

        var showcase = profiles.EnumerateArray().Single();
        showcase.GetProperty("name").GetString().Should().Be("showcase");

        // The showcase profile demonstrates the database-level config capabilities:
        // a polymorphic join, soft-delete shaping, and hidden columns.
        var metadata = showcase.GetProperty("metadata")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();

        metadata.Should().Contain(m => m!.Contains("polymorphic-map: company=companies"));
        metadata.Should().Contain(m => m!.Contains("soft-delete: deleted_at"));
        metadata.Should().Contain(m => m!.Contains("visibility: hidden"));

        var modules = showcase.GetProperty("modules")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();
        modules.Should().BeEquivalentTo("polymorphic", "soft-delete");
    }

    [Fact]
    public void LoadSampleConfig_Blog_ReturnsNull()
    {
        // The blog quickstart ships no bundled profile config.
        QuickstartSchemas.LoadSampleConfig("blog").Should().BeNull();
    }
}
