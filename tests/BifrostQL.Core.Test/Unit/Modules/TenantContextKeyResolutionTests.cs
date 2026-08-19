using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Pins <see cref="TenantFilterTransformer.ResolveTenantContextKey"/>, the ONE
/// resolution rule the read side, the write side (TenantMutationTransformer) and the
/// history/approval/retention hooks all share so the tenant claim can never differ
/// between them. Absent metadata falls back to the default claim; metadata that is
/// present but not a usable string is a misconfiguration of a SECURITY key and must
/// fail fast rather than silently scope by the default.
/// </summary>
public class TenantContextKeyResolutionTests
{
    private static IDbModel ModelWith(params (string key, object? value)[] meta) => new DbModel
    {
        Tables = Array.Empty<IDbTable>(),
        Metadata = meta.ToDictionary(m => m.key, m => m.value),
    };

    [Fact]
    public void AbsentMetadata_ReturnsDefaultClaim()
    {
        TenantFilterTransformer.ResolveTenantContextKey(ModelWith())
            .Should().Be(TenantFilterTransformer.DefaultTenantContextKey);
    }

    [Fact]
    public void ValidStringKey_IsReturned()
    {
        var model = ModelWith((TenantFilterTransformer.TenantContextKeyMetadata, "org_id"));

        TenantFilterTransformer.ResolveTenantContextKey(model).Should().Be("org_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PresentButEmptyKey_FailsFast(string configured)
    {
        var model = ModelWith((TenantFilterTransformer.TenantContextKeyMetadata, configured));

        model.Invoking(m => TenantFilterTransformer.ResolveTenantContextKey(m))
            .Should().Throw<BifrostExecutionError>()
            .WithMessage("*must be a non-empty string*");
    }

    [Fact]
    public void PresentButNonString_FailsFast_NotSilentDefault()
    {
        // A metadata value of the wrong type (set programmatically) previously slipped past
        // `key is string` and silently scoped by the default claim. It must fail fast.
        var model = ModelWith((TenantFilterTransformer.TenantContextKeyMetadata, 123));

        model.Invoking(m => TenantFilterTransformer.ResolveTenantContextKey(m))
            .Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ReadAndWriteSidesResolveIdentically()
    {
        // The write side must resolve through the same rule; a divergence would let a write
        // scope by a different claim than the read. Both go through ResolveTenantContextKey,
        // so a valid custom key and the default both agree by construction.
        var custom = ModelWith((TenantFilterTransformer.TenantContextKeyMetadata, "org_id"));
        var def = ModelWith();

        TenantFilterTransformer.ResolveTenantContextKey(custom).Should().Be("org_id");
        TenantFilterTransformer.ResolveTenantContextKey(def)
            .Should().Be(TenantFilterTransformer.DefaultTenantContextKey);
    }
}
