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
    public void LoadSampleConfig_Crm_ParsesSalesAndAdminProfiles()
    {
        // Act
        var json = QuickstartSchemas.LoadSampleConfig("crm");

        // Assert: the embedded resource exists and deserializes to two profiles.
        json.Should().NotBeNull();

        var doc = JsonDocument.Parse(json!);
        var profiles = doc.RootElement.GetProperty("profiles");
        profiles.GetArrayLength().Should().Be(2);

        var names = profiles.EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToArray();
        names.Should().BeEquivalentTo("sales", "admin");

        // The sales profile hides curated columns and opts into the notes join.
        var sales = profiles.EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "sales");
        var salesMetadata = sales.GetProperty("metadata")
            .EnumerateArray()
            .Select(m => m.GetString())
            .ToArray();

        salesMetadata.Should().Contain(m => m!.Contains("visibility: hidden"));
        salesMetadata.Should().Contain(m => m!.Contains("polymorphic-map: company=companies"));
    }

    [Fact]
    public void LoadSampleConfig_Blog_ReturnsNull()
    {
        // The blog quickstart ships no bundled profile config.
        QuickstartSchemas.LoadSampleConfig("blog").Should().BeNull();
    }
}
