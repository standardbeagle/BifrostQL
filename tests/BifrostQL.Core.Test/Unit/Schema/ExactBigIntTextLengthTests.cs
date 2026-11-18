using System.Numerics;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQLParser.AST;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// <c>BigInt</c> text is parsed with <see cref="BigInteger"/>, whose cost grows faster than
/// linearly with the digit count, so text past <see cref="ExactNumericScalars.MaxNumberTextLength"/>
/// is refused before parsing. A 40-digit value, well inside the cap, stays exact.
/// </summary>
public sealed class ExactBigIntTextLengthTests
{
    private const string FortyDigits = "1234567890123456789012345678901234567891";
    private static readonly string OverCap = new('9', ExactNumericScalars.MaxNumberTextLength + 1);
    private static readonly string AtCap = new('9', ExactNumericScalars.MaxNumberTextLength);

    [Fact]
    public void TextOverTheCap_IsRefusedAsAVariable()
    {
        var scalar = new ExactBigIntGraphType();

        scalar.CanParseValue(OverCap).Should().BeFalse();
        var act = () => scalar.ParseValue(OverCap);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TextOverTheCap_IsRefusedAsALiteral()
    {
        var scalar = new ExactBigIntGraphType();
        var literal = new GraphQLIntValue(OverCap);

        scalar.CanParseLiteral(literal).Should().BeFalse();
        var act = () => scalar.ParseLiteral(literal);
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Control: the cap itself is inclusive, so the refusal above is the length, not the digits.</summary>
    [Fact]
    public void TextAtTheCap_Parses()
    {
        new ExactBigIntGraphType().ParseValue(AtCap).Should().Be(BigInteger.Parse(AtCap));
    }

    [Fact]
    public void FortyDigitText_StaysExact()
    {
        var parsed = new ExactBigIntGraphType().ParseValue(FortyDigits);

        parsed.Should().BeOfType<BigInteger>().Which.ToString().Should().Be(FortyDigits);
    }
}
