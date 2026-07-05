using BifrostQL.Core.Utils;
using FluentAssertions;

namespace BifrostQL.Core.Test.Unit.Utils;

/// <summary>
/// Pins the fail-fast contract of numeric metadata parsing: an absent value uses
/// the caller's default (explicitly asked for), but a present-but-invalid value
/// is a configuration error and throws rather than silently reverting to the
/// default. A silent revert would hide operator typos.
/// </summary>
public sealed class MetadataNumberTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PositiveInt_AbsentValue_ReturnsDefault(string? value)
    {
        MetadataNumber.PositiveInt(value, 42, "max-rows").Should().Be(42);
    }

    [Fact]
    public void PositiveInt_ValidValue_ReturnsParsed()
    {
        MetadataNumber.PositiveInt("100", 42, "max-rows").Should().Be(100);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1O0")]      // letter O
    [InlineData("0")]        // not positive
    [InlineData("-5")]       // not positive
    [InlineData("3.5")]      // not an integer
    public void PositiveInt_InvalidValue_Throws(string value)
    {
        var act = () => MetadataNumber.PositiveInt(value, 42, "max-rows");

        act.Should().Throw<InvalidOperationException>().WithMessage("*max-rows*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PositiveIntOrNull_AbsentValue_ReturnsNull(string? value)
    {
        MetadataNumber.PositiveIntOrNull(value, "maxlength").Should().BeNull();
    }

    [Fact]
    public void PositiveIntOrNull_ValidValue_ReturnsParsed()
    {
        MetadataNumber.PositiveIntOrNull("255", "maxlength").Should().Be(255);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public void PositiveIntOrNull_InvalidValue_Throws(string value)
    {
        var act = () => MetadataNumber.PositiveIntOrNull(value, "maxlength");

        act.Should().Throw<InvalidOperationException>().WithMessage("*maxlength*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PositiveLong_AbsentValue_ReturnsDefault(string? value)
    {
        MetadataNumber.PositiveLong(value, 10_485_760, "maxSize").Should().Be(10_485_760);
    }

    [Fact]
    public void PositiveLong_ValidValue_ReturnsParsed()
    {
        MetadataNumber.PositiveLong("5242880", 10, "maxSize").Should().Be(5242880);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-9")]
    public void PositiveLong_InvalidValue_Throws(string value)
    {
        var act = () => MetadataNumber.PositiveLong(value, 10, "maxSize");

        act.Should().Throw<InvalidOperationException>().WithMessage("*maxSize*");
    }
}
