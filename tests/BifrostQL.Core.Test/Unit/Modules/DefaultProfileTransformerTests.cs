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
/// side. Profile gating is band-limited: only application-band modules
/// (priority >= <see cref="BifrostProfile.ApplicationPriorityFloor"/>) are
/// toggleable — a profile, even one that explicitly opts out of everything
/// (Modules = []), can never strip a security-band transformer.
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

    /// <summary>An application-band transformer — the only band profiles may toggle.</summary>
    private sealed class NamedAppFilter : IFilterTransformer, IModuleNamed
    {
        public string ModuleName => "app-filter";
        public int Priority => BifrostProfile.ApplicationPriorityFloor;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => true;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    [Fact]
    public void ExplicitEmptyModulesProfile_StripsOnlyApplicationBandTransformers()
    {
        // Arrange: a curated profile that explicitly opts out of all modules,
        // over a source containing a security-band module (priority 0), an
        // application-band module (priority 200), and an unnamed transformer.
        var optOut = new BifrostProfile { Name = "direct", Modules = System.Array.Empty<string>() };
        var source = new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new NamedTenantFilter(), new NamedAppFilter(), new UnnamedTransformer() }
        };

        // Act
        var filtered = BifrostProfileRegistry.FilterBy(source, optOut);

        // Assert: fail-closed — the security-band tenant filter survives even
        // an explicit opt-out (a client-selectable profile must never strip
        // tenant isolation); only the application-band module is stripped.
        filtered.Count.Should().Be(2);
        filtered.OfType<NamedTenantFilter>().Should().ContainSingle();
        filtered.OfType<NamedAppFilter>().Should().BeEmpty();
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
