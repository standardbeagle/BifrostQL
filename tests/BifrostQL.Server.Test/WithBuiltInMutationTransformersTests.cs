using System;
using System.Linq;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Server;
using Xunit;

namespace BifrostQL.Server.Test
{
    public class WithBuiltInMutationTransformersTests
    {
        [Fact]
        public void EmptyConfigured_RegistersAllBuiltInMutationTransformers()
        {
            var result = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                Array.Empty<IMutationTransformer>());

            Assert.Contains(result, t => t is PolicyMutationTransformer);
            Assert.Contains(result, t => t is StateMachineMutationTransformer);
            Assert.Contains(result, t => t is EnumValueMutationTransformer);
            Assert.Contains(result, t => t is ExtendedServerValidationTransformer);
            Assert.Contains(result, t => t is SoftDeleteMutationTransformer);
            Assert.Contains(result, t => t is ConcurrencyMutationTransformer);
        }

        [Fact]
        public void CallerSuppliedConcurrency_TakesPrecedence_NoDuplicate()
        {
            var mine = new ConcurrencyMutationTransformer();

            var result = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                new IMutationTransformer[] { mine });

            Assert.Same(mine, Assert.Single(result, t => t is ConcurrencyMutationTransformer));
        }

        [Fact]
        public void CallerSuppliedSoftDelete_TakesPrecedence_NoDuplicate()
        {
            var mine = new SoftDeleteMutationTransformer();

            var result = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                new IMutationTransformer[] { mine });

            // Exactly one soft-delete mutation transformer, and it is the caller's.
            Assert.Same(mine, Assert.Single(result, t => t is SoftDeleteMutationTransformer));

            // The other built-ins are still auto-registered alongside it.
            Assert.Contains(result, t => t is PolicyMutationTransformer);
            Assert.Contains(result, t => t is StateMachineMutationTransformer);
            Assert.Contains(result, t => t is EnumValueMutationTransformer);
            Assert.Contains(result, t => t is ExtendedServerValidationTransformer);
        }

        [Fact]
        public void SoftDeleteMutationTransformer_ImplementsIModuleNamed_ForProfileOptOut()
        {
            var result = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                Array.Empty<IMutationTransformer>());

            var softDelete = Assert.Single(result, t => t is SoftDeleteMutationTransformer);
            Assert.IsAssignableFrom<IModuleNamed>(softDelete);
        }
    }
}
