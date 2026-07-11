using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Regression coverage for the write-side profile filter that closes the read/write
/// asymmetry: mutation resolvers used to pull <see cref="IMutationTransformers"/> raw
/// from DI, so a named profile that toggled an application module on reads still ran it
/// on writes. The write paths now apply
/// <see cref="BifrostProfileRegistry.FilterBy(IMutationTransformers, IDictionary{string, object?})"/>
/// off the request's active profile, exactly like the read path. These tests pin the
/// convenience overload's contract and lock the reclassification of
/// <see cref="ExtendedServerValidationTransformer"/> into the non-toggleable band so a
/// client-selectable profile can never silently disable input validation on writes.
/// </summary>
public class NamedProfileWriteFilterTests
{
    /// <summary>An application-band mutation transformer (priority 200) a profile may toggle.</summary>
    private sealed class AppMutationTransformer : IMutationTransformer, IModuleNamed
    {
        public int Priority => 200;
        public string ModuleName => "app-report";
        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) => false;
        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
    }

    private static IMutationTransformers MutationSource() => new MutationTransformersWrap
    {
        Transformers = new IMutationTransformer[]
        {
            new TenantMutationTransformer(),            // priority 0   (security)
            new SoftDeleteMutationTransformer(),        // priority 100 (data-integrity)
            new ExtendedServerValidationTransformer(),  // priority 199 (data-integrity, default-on)
            new AppMutationTransformer(),               // priority 200 (application, opt-in)
        },
    };

    private static IDictionary<string, object?> UserContextWith(BifrostProfile? profile)
    {
        var uc = new Dictionary<string, object?>();
        if (profile != null)
            uc[BifrostProfile.UserContextKey] = profile;
        return uc;
    }

    [Fact]
    public void NoProfileStamped_RetainsEveryTransformer()
    {
        // A transport that never stamped a profile (no key present) must leave the full
        // set active — fail-closed for writes: keeping a guard can only tighten a mutation.
        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), UserContextWith(null));

        filtered.Should().Contain(t => t is TenantMutationTransformer);
        filtered.Should().Contain(t => t is SoftDeleteMutationTransformer);
        filtered.Should().Contain(t => t is ExtendedServerValidationTransformer);
        filtered.Should().Contain(t => t is AppMutationTransformer,
            "with no profile the write path must not strip anything");
    }

    [Fact]
    public void DefaultProfile_StripsAppBandButKeepsSecurityDataIntegrityAndValidation()
    {
        // The empty "default" profile is what both transports stamp when no profile is
        // requested. It strips opt-in application modules but must retain every default-on
        // guard — including server validation, which now sits below the application floor.
        var uc = UserContextWith(new BifrostProfile { Name = "default", Modules = Array.Empty<string>() });

        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), uc);

        filtered.Should().Contain(t => t is TenantMutationTransformer);
        filtered.Should().Contain(t => t is SoftDeleteMutationTransformer);
        filtered.Should().Contain(t => t is ExtendedServerValidationTransformer,
            "server validation is on by default and must survive the default profile");
        filtered.Should().NotContain(t => t is AppMutationTransformer,
            "application-band modules are opt-in and absent under the default profile");
    }

    [Fact]
    public void NamedProfileEnablingAppModule_RetainsItOnWrites()
    {
        // The reported bug's positive case: a named profile that enables the application
        // module runs it on writes, symmetric with reads.
        var uc = UserContextWith(new BifrostProfile { Name = "reporting", Modules = new[] { "app-report" } });

        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), uc);

        filtered.Should().Contain(t => t is AppMutationTransformer,
            "the profile explicitly enabled the application module");
        filtered.Should().Contain(t => t is TenantMutationTransformer);
    }

    [Fact]
    public void NamedProfileOmittingAppModule_StripsItOnWrites()
    {
        // The reported bug itself: a named profile that omits the application module used
        // to still enforce it on writes; now it is stripped on writes too.
        var uc = UserContextWith(new BifrostProfile { Name = "curated", Modules = new[] { "something-else" } });

        var filtered = BifrostProfileRegistry.FilterBy(MutationSource(), uc);

        filtered.Should().NotContain(t => t is AppMutationTransformer,
            "an application module absent from the profile's list must be disabled on writes");
        filtered.Should().Contain(t => t is TenantMutationTransformer);
        filtered.Should().Contain(t => t is SoftDeleteMutationTransformer);
        filtered.Should().Contain(t => t is ExtendedServerValidationTransformer,
            "a named profile cannot strip the default-on validation guard");
    }

    [Fact]
    public void ServerValidation_SitsBelowTheApplicationFloor()
    {
        // Locks the reclassification: validation is default-on and opts out only per-table
        // via metadata, so it must be non-toggleable (below the application floor) like every
        // other default-on built-in — otherwise a client-selectable profile could disable it.
        var validation = new ExtendedServerValidationTransformer();

        validation.Priority.Should().BeLessThan(BifrostProfile.ApplicationPriorityFloor);
        BifrostProfile.IsProfileToggleable(validation.Priority).Should().BeFalse();
    }
}
