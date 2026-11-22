using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Modules;

/// <summary>
/// <see cref="TenantFilterTransformer.ReadTenantValue(IDictionary{string, object?}, string)"/>, the
/// one reader every tenant-stamping store and the policy identity read the tenant through. Two
/// orgs whose raw claims arrive as one-element <c>string[]</c> must read as two tenants.
/// </summary>
public sealed class TenantValueReaderTests
{
    private const string Key = "org_id";

    [Fact]
    public void OneElementRawClaims_OfTwoOrgs_ReadAsTwoTenants()
    {
        var acme = Read(new[] { "acme" });
        var globex = Read(new[] { "globex" });

        acme.Should().Be("acme");
        globex.Should().Be("globex");
    }

    [Fact]
    public void PolicyIdentity_ReadsTheRawClaimTenantThroughTheSameReader()
    {
        var context = new Dictionary<string, object?> { ["user_id"] = "u-1", [Key] = new[] { "acme" } };

        PolicyIdentity.FromUserContext(context, Key).TenantId.Should().Be("acme");
    }

    [Theory]
    [MemberData(nameof(NonScalarTenants))]
    public void NonScalarTenant_IsRefused(object value)
    {
        var act = () => Read(value);

        act.Should().Throw<BifrostExecutionError>()
            .Where(e => e.ErrorCode == BifrostExecutionError.AccessDeniedCode)
            .WithMessage(TenantFilterTransformer.NonScalarTenantMessage);
    }

    public static TheoryData<object> NonScalarTenants() => new()
    {
        new[] { "acme", "globex" },
        Array.Empty<string>(),
        new List<string> { "acme", "globex" },
        JsonDocument.Parse("""["acme","globex"]""").RootElement.Clone(),
        JsonDocument.Parse("""{"org":"acme"}""").RootElement.Clone(),
        // An opaque object's ToString() is its type name, one value for every caller.
        new ClaimsPrincipal(),
    };

    [Fact]
    public void AbsentOrNullTenant_ReadsAsNull()
    {
        TenantFilterTransformer.ReadTenantValue(new Dictionary<string, object?>(), Key).Should().BeNull();
        Read(null).Should().BeNull();
    }

    [Fact]
    public void NumericTenant_FormatsWithTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            1.5m.ToString().Should().Be("1,5", "control: the ambient culture formats differently");
            Read(1.5m).Should().Be("1.5");
            Read(42L).Should().Be("42");
            Read(JsonDocument.Parse("42").RootElement.Clone()).Should().Be("42");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string? Read(object? value)
        => TenantFilterTransformer.ReadTenantValue(new Dictionary<string, object?> { [Key] = value }, Key);
}
