using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Covers the reserved '.'-prefixed profile namespace:
/// - registration of any '.'-prefixed name fails fast (criterion 3);
/// - the synthetic <see cref="ProfileNames.System.Default"/> keeps
///   <c>Modules = Array.Empty&lt;string&gt;()</c> (NOT null), so a &gt;= 200-priority
///   application-band opt-in module is INACTIVE while a &lt; 200 security-band module
///   stays ACTIVE — proving the empty-array (not the looser null) semantics (criterion 4).
/// </summary>
public class ReservedProfileNamespaceTests
{
    /// <summary>An application-band opt-in (priority 200) a profile may toggle off.</summary>
    private sealed class AppBandFilter : IFilterTransformer, IModuleNamed
    {
        public int Priority => 200;
        public string ModuleName => "app-report";
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    /// <summary>A security-band module (priority 0) that no profile may toggle off.</summary>
    private sealed class SecurityBandFilter : IFilterTransformer, IModuleNamed
    {
        public int Priority => 0;
        public string ModuleName => "tenant-guard";
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    private static IFilterTransformers Source() => new FilterTransformersWrap
    {
        Transformers = new IFilterTransformer[] { new SecurityBandFilter(), new AppBandFilter() },
    };

    // --- Criterion 3: registration validation ------------------------------------------

    [Fact]
    public void Add_SystemReservedName_FailsFast_NamingTheProfile()
    {
        var registry = new BifrostProfileRegistry();

        var act = () => registry.Add(new BifrostProfile { Name = ProfileNames.System.Default });

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*{ProfileNames.System.Default}*system-reserved*");
        registry.HasProfiles.Should().BeFalse("a rejected registration must not be silently kept");
    }

    [Fact]
    public void Add_AnyDotPrefixedName_FailsFast()
    {
        var registry = new BifrostProfileRegistry();

        var act = () => registry.Add(new BifrostProfile { Name = ".secret" });

        act.Should().Throw<ArgumentException>().WithMessage("*.secret*not registrable*");
    }

    [Fact]
    public void ReplaceAll_WithReservedName_FailsFast()
    {
        var registry = new BifrostProfileRegistry();

        var act = () => registry.ReplaceAll(new[]
        {
            new BifrostProfile { Name = "sales" },
            new BifrostProfile { Name = ".default" },
        });

        act.Should().Throw<ArgumentException>().WithMessage("*.default*system-reserved*");
    }

    [Fact]
    public void Add_OrdinaryName_IsAccepted()
    {
        var registry = new BifrostProfileRegistry();

        registry.Add(new BifrostProfile { Name = "reporting" });

        registry.Get("reporting").Should().NotBeNull();
    }

    // --- Criterion 4: empty-array (not null) semantics for .default ---------------------

    [Fact]
    public void SystemDefault_EmptyModules_StripsAppBand_RetainsSecurityBand()
    {
        // The synthetic .default profile: empty (non-null) module list.
        var systemDefault = new BifrostProfile { Name = ProfileNames.System.Default, Modules = Array.Empty<string>() };

        var filtered = BifrostProfileRegistry.FilterBy(Source(), systemDefault);

        filtered.Should().Contain(t => t is SecurityBandFilter,
            "security-band (< 200) modules are always retained regardless of the profile's list");
        filtered.Should().NotContain(t => t is AppBandFilter,
            "application-band (>= 200) opt-in modules are stripped by the empty-array default");

        // IsModuleActive proves the same at the profile level.
        systemDefault.IsModuleActive(new AppBandFilter()).Should().BeFalse();
    }

    [Fact]
    public void NullModules_WouldActivateAppBand_ProvingEmptyArrayIsTheTighterValue()
    {
        // Contrast: a null module list is the LOOSER value — FilterBy short-circuits and
        // every application-band opt-in activates. This is exactly what .default must NOT be.
        var looseDefault = new BifrostProfile { Name = "would-be-loose", Modules = null };

        var filtered = BifrostProfileRegistry.FilterBy(Source(), looseDefault);

        filtered.Should().Contain(t => t is AppBandFilter,
            "a null module list activates every application-band opt-in — the looser semantics");
        looseDefault.IsModuleActive(new AppBandFilter()).Should().BeTrue();
    }
}
