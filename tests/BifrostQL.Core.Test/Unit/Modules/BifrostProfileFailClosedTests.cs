using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Regression coverage for the fail-closed profile filter: a client-selectable profile
/// (including the empty "default" profile, whose name is fully client-controlled) must
/// never be able to strip security or data-integrity modules — tenant isolation and
/// soft-delete filtering in particular. Only application-band modules (priority &gt;=
/// <see cref="BifrostProfile.ApplicationPriorityFloor"/>) may be toggled by profile.
/// </summary>
public class BifrostProfileFailClosedTests
{
    /// <summary>An application-band filter transformer (priority 200) that a profile may toggle.</summary>
    private sealed class AppFilterTransformer : IFilterTransformer, IModuleNamed
    {
        public int Priority => 200;
        public string ModuleName => "app-report";
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;
    }

    /// <summary>An application-band mutation transformer (priority 200) that a profile may toggle.</summary>
    private sealed class AppMutationTransformer : IMutationTransformer, IModuleNamed
    {
        public int Priority => 200;
        public string ModuleName => "app-report";
        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => false;
        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
    }

    /// <summary>A named query observer (e.g. an audit hook) that a profile might try to disable.</summary>
    private sealed class NamedAuditObserver : IQueryObserver, IModuleNamed
    {
        public string ModuleName => "audit";
        public QueryPhase[] Phases => new[] { QueryPhase.AfterExecute };
        public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context) => ValueTask.CompletedTask;
    }

    private static IQueryObservers ObserverSource() => new QueryObserversWrap
    {
        Observers = new IQueryObserver[] { new NamedAuditObserver() },
    };

    private static IFilterTransformers FilterSource() => new FilterTransformersWrap
    {
        Transformers = new IFilterTransformer[]
        {
            new TenantFilterTransformer(),      // priority 0   (security)
            new SoftDeleteFilterTransformer(),  // priority 100 (data-integrity)
            new AppFilterTransformer(),         // priority 200 (application)
        },
    };

    private static IMutationTransformers MutationSource() => new MutationTransformersWrap
    {
        Transformers = new IMutationTransformer[]
        {
            new TenantMutationTransformer(),      // priority 0   (security)
            new SoftDeleteMutationTransformer(),  // priority 100 (data-integrity)
            new AppMutationTransformer(),         // priority 200 (application)
        },
    };

    [Fact]
    public void DefaultProfile_StillAppliesTenantAndSoftDeleteFilters()
    {
        // The empty "default" profile has a non-null, empty module list — the exact
        // profile the HTTP/binary transports build when no profile is requested.
        var defaultProfile = new BifrostProfile { Name = "default", Modules = Array.Empty<string>() };

        var filtered = BifrostProfileRegistry.FilterBy(FilterSource(), defaultProfile);

        filtered.Should().Contain(t => t is TenantFilterTransformer,
            "tenant isolation must survive the default profile");
        filtered.Should().Contain(t => t is SoftDeleteFilterTransformer,
            "soft-delete filtering must survive the default profile");
        filtered.Should().NotContain(t => t is AppFilterTransformer,
            "application-band modules are opt-in and absent under the default profile");
    }

    [Fact]
    public void NamedProfileWithoutSecurityModules_StillAppliesTenantAndSoftDeleteFilters()
    {
        // A named profile that enables only an application module must NOT be able to
        // drop the security/data-integrity modules by omitting them.
        var profile = new BifrostProfile { Name = "reporting", Modules = new[] { "app-report" } };

        var filtered = BifrostProfileRegistry.FilterBy(FilterSource(), profile);

        filtered.Should().Contain(t => t is TenantFilterTransformer);
        filtered.Should().Contain(t => t is SoftDeleteFilterTransformer);
        filtered.Should().Contain(t => t is AppFilterTransformer,
            "the profile explicitly enabled the application module");
    }

    [Fact]
    public void DefaultProfile_StillAppliesTenantAndSoftDeleteMutationGuards()
    {
        var defaultProfile = new BifrostProfile { Name = "default", Modules = Array.Empty<string>() };

        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), defaultProfile);

        filtered.Should().Contain(t => t is TenantMutationTransformer,
            "the tenant write guard must survive the default profile");
        filtered.Should().Contain(t => t is SoftDeleteMutationTransformer,
            "the soft-delete write guard must survive the default profile");
        filtered.Should().NotContain(t => t is AppMutationTransformer);
    }

    [Fact]
    public void NamedProfile_TogglesOnlyApplicationBandModules()
    {
        // Not listing the application module removes it; the security/data modules stay.
        var withoutApp = new BifrostProfile { Name = "curated", Modules = new[] { "something-else" } };

        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), withoutApp);

        filtered.Should().Contain(t => t is TenantMutationTransformer);
        filtered.Should().Contain(t => t is SoftDeleteMutationTransformer);
        filtered.Should().NotContain(t => t is AppMutationTransformer,
            "an application module absent from the profile's list is disabled");
    }

    [Fact]
    public void DefaultProfile_StillRetainsNamedQueryObservers()
    {
        // Observers carry no Priority band, so they are fail-closed: a named audit
        // observer must survive the empty "default" profile (whose name is fully
        // client-controlled) rather than being silently stripped like the pre-fix
        // IsModuleActive filtering did.
        var defaultProfile = new BifrostProfile { Name = "default", Modules = Array.Empty<string>() };

        var filtered = BifrostProfileRegistry.FilterBy(ObserverSource(), defaultProfile);

        filtered.Should().Contain(o => o is NamedAuditObserver,
            "audit/lifecycle observers cannot be disabled by profile selection");
    }

    [Fact]
    public void NamedProfileOmittingObserver_StillRetainsIt()
    {
        // Even a named profile that lists other modules must not drop the observer.
        var profile = new BifrostProfile { Name = "reporting", Modules = new[] { "something-else" } };

        var filtered = BifrostProfileRegistry.FilterBy(ObserverSource(), profile);

        filtered.Should().Contain(o => o is NamedAuditObserver);
    }

    [Theory]
    [InlineData(0, false)]    // tenant band
    [InlineData(50, false)]   // audit band
    [InlineData(100, false)]  // soft-delete / data-integrity band
    [InlineData(199, false)]  // top of data-integrity band
    [InlineData(200, true)]   // application band floor
    [InlineData(500, true)]   // application band
    public void IsProfileToggleable_MatchesTheApplicationBandFloor(int priority, bool expected)
    {
        BifrostProfile.IsProfileToggleable(priority).Should().Be(expected);
    }
}
