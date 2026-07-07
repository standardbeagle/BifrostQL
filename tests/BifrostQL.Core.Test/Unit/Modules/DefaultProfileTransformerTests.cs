using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Tests that pin down the fix for the "default profile strips read-side
/// security transformers" hole. The synthetic no-profile fallback in
/// BifrostHttpMiddleware now uses Modules = null (all modules active), so an
/// unconfigured endpoint keeps every registered IModuleNamed transformer
/// (tenant-filter, soft-delete, policy) on READS — symmetric with the write
/// side. A named profile that explicitly opts out (Modules = []) must still
/// strip.
/// </summary>
public sealed class DefaultProfileTransformerTests
{
    /// <summary>A security transformer that participates in profile gating.</summary>
    private sealed class NamedTenantFilter : IFilterTransformer, IModuleNamed
    {
        public string ModuleName => "tenant-filter";
        public int Priority => 0;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => true;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    /// <summary>A transformer with no module name — always active regardless of profile.</summary>
    private sealed class UnnamedTransformer : IFilterTransformer
    {
        public int Priority => 0;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => true;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    private static IFilterTransformers Source() => new FilterTransformersWrap
    {
        Transformers = new IFilterTransformer[] { new NamedTenantFilter(), new UnnamedTransformer() }
    };

    [Fact]
    public void SyntheticDefaultFallback_UsesNullModules_KeepsAllTransformersActive()
    {
        // Arrange: the exact synthetic profile the middleware builds when no
        // profile is selected (null/empty/"default" with no registered default).
        var syntheticDefault = new BifrostProfile { Name = "default", Modules = null };

        // Act
        var filtered = BifrostProfileRegistry.FilterBy(Source(), syntheticDefault);

        // Assert: both the security (IModuleNamed) and the unnamed transformer
        // survive — reads stay scoped, matching the write side.
        filtered.Count.Should().Be(2);
        filtered.OfType<NamedTenantFilter>().Should().ContainSingle();
    }

    [Fact]
    public void ExplicitEmptyModulesProfile_StillStripsNamedTransformers()
    {
        // Arrange: a curated profile that explicitly opts out of all modules.
        var optOut = new BifrostProfile { Name = "direct", Modules = System.Array.Empty<string>() };

        // Act
        var filtered = BifrostProfileRegistry.FilterBy(Source(), optOut);

        // Assert: the named security transformer is stripped; only the unnamed
        // (always-active) transformer remains. This is the intended curated
        // behavior and must NOT change.
        filtered.Count.Should().Be(1);
        filtered.OfType<NamedTenantFilter>().Should().BeEmpty();
        filtered.OfType<UnnamedTransformer>().Should().ContainSingle();
    }

    [Fact]
    public void NamedProfileEnablingModule_KeepsThatTransformer()
    {
        // Arrange: a profile that opts the tenant filter back in by name.
        var scoped = new BifrostProfile { Name = "scoped", Modules = new[] { "tenant-filter" } };

        // Act
        var filtered = BifrostProfileRegistry.FilterBy(Source(), scoped);

        // Assert: named module present, plus the always-active unnamed one.
        filtered.Count.Should().Be(2);
        filtered.OfType<NamedTenantFilter>().Should().ContainSingle();
    }
}
