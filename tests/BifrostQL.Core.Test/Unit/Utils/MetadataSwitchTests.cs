using BifrostQL.Core.Utils;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Utils;

/// <summary>
/// Tests the uniform activate/deactivate vocabulary shared by every metadata
/// boolean toggle, plus the inline <c>!</c> negation used to prune a single
/// match out of a breadth-matched list value.
/// </summary>
public sealed class MetadataSwitchTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("enabled")]
    [InlineData("1")]
    [InlineData("activate")]
    [InlineData("ACTIVE")]
    public void Parse_OnTokens_ReturnTrue(string value)
    {
        MetadataSwitch.Parse(value, defaultValue: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("disabled")]
    [InlineData("0")]
    [InlineData("deactivate")]
    [InlineData("!")]
    [InlineData("!anything")]
    public void Parse_OffTokens_ReturnFalse(string value)
    {
        MetadataSwitch.Parse(value, defaultValue: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unrecognized")]
    public void Parse_BlankOrUnknown_ReturnsDefault(string? value)
    {
        MetadataSwitch.Parse(value, defaultValue: true).Should().BeTrue();
        MetadataSwitch.Parse(value, defaultValue: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("!Roles", true)]
    [InlineData("  !Roles:UserRoles", true)]
    [InlineData("Roles:UserRoles", false)]
    [InlineData("", false)]
    public void IsNegated_DetectsLeadingBang(string entry, bool expected)
    {
        MetadataSwitch.IsNegated(entry).Should().Be(expected);
    }

    [Theory]
    [InlineData("!Roles:UserRoles", "Roles:UserRoles")]
    [InlineData("  ! Roles ", "Roles")]
    [InlineData("Roles:UserRoles", "Roles:UserRoles")]
    public void StripNegation_RemovesLeadingBang(string entry, string expected)
    {
        MetadataSwitch.StripNegation(entry).Should().Be(expected);
    }
}
