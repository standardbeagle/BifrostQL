using System;
using System.Linq;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class WithBuiltInFilterTransformersTests
    {
        [Fact]
        public void EmptyConfigured_RegistersAllBuiltInSecurityFilters()
        {
            var result = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(
                Array.Empty<IFilterTransformer>());

            Assert.Contains(result, t => t is PolicyFilterTransformer);
            Assert.Contains(result, t => t is TenantFilterTransformer);
            Assert.Contains(result, t => t is SoftDeleteFilterTransformer);
            Assert.Contains(result, t => t is AutoFilterTransformer);
        }

        [Fact]
        public void CallerSuppliedInstance_TakesPrecedence_NoDuplicateBuiltIn()
        {
            var mine = new TenantFilterTransformer();

            var result = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(
                new IFilterTransformer[] { mine });

            // Exactly one tenant filter, and it is the caller's instance.
            Assert.Same(mine, Assert.Single(result, t => t is TenantFilterTransformer));

            // The other built-ins are still auto-registered — suppressing one
            // caller-supplied type must not drop the rest.
            Assert.Contains(result, t => t is PolicyFilterTransformer);
            Assert.Contains(result, t => t is SoftDeleteFilterTransformer);
            Assert.Contains(result, t => t is AutoFilterTransformer);
        }

        [Fact]
        public void CallerSuppliedTransformers_ArePreserved()
        {
            var configured = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(),
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(),
                new AutoFilterTransformer(),
            };

            var result = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(configured);

            // No built-in re-added: each type appears exactly once.
            Assert.Single(result, t => t is PolicyFilterTransformer);
            Assert.Single(result, t => t is TenantFilterTransformer);
            Assert.Single(result, t => t is SoftDeleteFilterTransformer);
            Assert.Single(result, t => t is AutoFilterTransformer);
        }

        [Fact]
        public void BuiltInFilters_ImplementIModuleNamed_ForProfileOptOut()
        {
            var result = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(
                Array.Empty<IFilterTransformer>());

            // Profile opt-out (BifrostProfileRegistry.FilterBy) keys off IModuleNamed.
            Assert.All(result, t => Assert.IsAssignableFrom<IModuleNamed>(t));
        }
    }
}
