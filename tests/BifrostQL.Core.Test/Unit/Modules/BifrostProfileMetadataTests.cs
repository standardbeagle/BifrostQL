using BifrostQL.Core.Modules;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class BifrostProfileMetadataTests
{
    [Fact]
    public void Profile_CarriesOwnMetadataRules()
    {
        var p = new BifrostProfile
        {
            Name = "sales",
            Modules = new[] { "polymorphic" },
            Metadata = new[] { "*.notes { polymorphic-id-column: entity_id }" },
        };

        p.Metadata.Should().ContainSingle()
            .Which.Should().Contain("polymorphic-id-column: entity_id");
    }

    [Fact]
    public void Profile_DefaultsToNoMetadata()
    {
        var p = new BifrostProfile { Name = "dev" };
        (p.Metadata ?? System.Array.Empty<string>()).Should().BeEmpty();
    }
}
