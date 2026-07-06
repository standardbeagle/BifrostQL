using BifrostQL.Core.QueryModel;
using FluentAssertions;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Locks <see cref="SqlParameterNames.Sanitize"/>, the single function both the
/// SQL-text placeholder (<see cref="ISqlDialect.AssignmentPlaceholder"/> and the
/// mutation WHERE builders) and the parameter binder
/// (<c>DbParameterBinder.AddParameters</c>) route column names through. If the
/// two sides ever disagree, writes to a table whose key/column name is not a
/// valid ADO parameter identifier (e.g. "Order Date") break at runtime.
/// </summary>
public class SqlParameterNamesTests
{
    [Theory]
    [InlineData("Id")]
    [InlineData("order_no")]
    [InlineData("_private")]
    [InlineData("Col123")]
    public void Sanitize_ValidIdentifier_PassesThroughUnchanged(string name)
    {
        // A name already valid as a parameter identifier must be returned as-is,
        // so the overwhelming common case adds no hash suffix and stays readable.
        SqlParameterNames.Sanitize(name).Should().Be(name);
    }

    [Theory]
    [InlineData("Order Date")]
    [InlineData("first-name")]
    [InlineData("amount($)")]
    [InlineData("col.with.dots")]
    public void Sanitize_InvalidCharacters_ProducesValidParameterIdentifier(string name)
    {
        var result = SqlParameterNames.Sanitize(name);

        result.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
        result.Should().NotContain(" ");
    }

    [Fact]
    public void Sanitize_LeadingDigit_PrefixedToStayValid()
    {
        SqlParameterNames.Sanitize("2nd column").Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
    }

    [Fact]
    public void Sanitize_NamesThatCollapseToSameBase_StayDistinct()
    {
        // "Order Date" and "Order_Date" both scrub to "Order_Date"; the appended
        // hash of the ORIGINAL name keeps them distinct so two columns never bind
        // the same parameter.
        var a = SqlParameterNames.Sanitize("Order Date");
        var b = SqlParameterNames.Sanitize("Order_Date");
        a.Should().NotBe(b);
    }

    [Fact]
    public void Sanitize_IsDeterministic()
    {
        // Both call sites (SQL text and binder) call Sanitize independently, so the
        // same input must always yield the same output.
        SqlParameterNames.Sanitize("Order Date").Should().Be(SqlParameterNames.Sanitize("Order Date"));
    }
}
