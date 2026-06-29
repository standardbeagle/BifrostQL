using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Model;

/// <summary>
/// Pins <see cref="ModelExtensions.ToGraphQl"/> — the SQL-identifier → GraphQL-name
/// sanitizer that the schema (graph) generator depends on. Every output must be a
/// valid GraphQL name (/[_A-Za-z][_0-9A-Za-z]*/) so the emitted SDL parses.
/// </summary>
public sealed class ToGraphQlNameTests
{
    private static readonly System.Text.RegularExpressions.Regex ValidGraphQlName =
        new("^[_A-Za-z][_0-9A-Za-z]*$");

    [Theory]
    [InlineData("Name", "name")]                 // simple, first char lowercased
    [InlineData("first name", "first_name")]     // space → underscore
    [InlineData("user-id", "user_id")]           // hyphen → underscore
    [InlineData("FullName", "fullName")]          // PascalCase → camelish
    public void CommonNames_MapAsExpected(string input, string expected)
    {
        input.ToGraphQl("col").Should().Be(expected);
    }

    [Fact]
    public void LeadingDigit_IsPrefixed()
    {
        // "1st" starts with a digit → "_1st" → leading-underscore → prefixed.
        var result = "1st".ToGraphQl("col");
        result.Should().Be("col_1st");
        ValidGraphQlName.IsMatch(result).Should().BeTrue();
    }

    [Fact]
    public void LeadingUnderscore_GetsPrefix()
    {
        "_id".ToGraphQl("col").Should().Be("col_id");
    }

    [Fact]
    public void SpecialCharacters_BecomeHexEscapes()
    {
        // '$' (0x24) is not alphanumeric, so it is hex-escaped as "_24".
        var result = "Total$".ToGraphQl("col");
        result.Should().Be("total_24");
        ValidGraphQlName.IsMatch(result).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                              // empty input (the former crash)
    [InlineData("first name")]
    [InlineData("user-id")]
    [InlineData("123")]
    [InlineData("_")]
    [InlineData("café")]                          // non-ASCII
    [InlineData("@weird#name!")]
    [InlineData("   ")]                            // all whitespace
    public void AnyInput_ProducesAValidGraphQlName(string input)
    {
        var result = input.ToGraphQl("col");
        result.Should().NotBeNullOrEmpty();
        ValidGraphQlName.IsMatch(result).Should()
            .BeTrue($"'{input}' produced '{result}', which is not a valid GraphQL name");
    }

    [Fact]
    public void EmptyInput_FallsBackToPrefix()
    {
        "".ToGraphQl("col").Should().Be("col");
        "".ToGraphQl("").Should().Be("_");
    }
}
