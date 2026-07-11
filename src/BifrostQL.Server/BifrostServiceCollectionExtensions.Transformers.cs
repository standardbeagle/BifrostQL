using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Appends instances of the supplied <paramref name="types"/>, resolved from
        /// <paramref name="sp"/>, to the caller-configured collection. Backs the generic
        /// <c>AddFilterTransformer&lt;T&gt;</c>/<c>AddMutationTransformer&lt;T&gt;</c>/<c>AddQueryObserver&lt;T&gt;</c>
        /// overloads, which are additive over the collection/factory overloads. Returns the
        /// original collection unchanged when no generic types were registered.
        /// </summary>
        internal static IReadOnlyCollection<T> ResolveTransformers<T>(
            IReadOnlyCollection<T> configured,
            IReadOnlyList<Type> types,
            IServiceProvider sp)
        {
            if (types.Count == 0)
                return configured;

            var combined = new List<T>(configured);
            foreach (var type in types)
                combined.Add((T)sp.GetRequiredService(type));
            return combined;
        }

        /// <summary>
        /// Prepends the built-in filter transformers to the caller-supplied set so
        /// that filtering metadata is enforced on the query path without explicit
        /// opt-in. <see cref="PolicyFilterTransformer"/>, <see cref="TenantFilterTransformer"/>,
        /// <see cref="SoftDeleteFilterTransformer"/>, and <see cref="AutoFilterTransformer"/>
        /// are each metadata-driven and a no-op for tables lacking their respective
        /// metadata key, so always registering them is safe. This closes a security
        /// footgun where <c>tenant-filter</c> metadata silently did nothing unless the
        /// host also registered the matching transformer by hand.
        ///
        /// A caller-supplied instance of the same type (e.g. a
        /// <see cref="PolicyFilterTransformer"/> configured with a non-default admin
        /// role) takes precedence and suppresses the built-in for that type. Per-profile
        /// opt-out is handled downstream by <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
        /// since each built-in implements <see cref="IModuleNamed"/>.
        /// </summary>
        internal static IReadOnlyCollection<IFilterTransformer> WithBuiltInFilterTransformers(
            IReadOnlyCollection<IFilterTransformer> configured)
        {
            // Keep the reserved security band (priority 0-99) for the host's built-ins:
            // a consumer filter below the floor would run ahead of tenant/policy filtering.
            ModulePriorityFloorGuard.EnsureConsumerPrioritiesRespectSecurityFloor(
                configured, t => t.Priority, "filter");

            var combined = new List<IFilterTransformer>();

            if (!configured.Any(t => t is PolicyFilterTransformer))
                combined.Add(new PolicyFilterTransformer());

            if (!configured.Any(t => t is TenantFilterTransformer))
                combined.Add(new TenantFilterTransformer());

            if (!configured.Any(t => t is SoftDeleteFilterTransformer))
                combined.Add(new SoftDeleteFilterTransformer());

            if (!configured.Any(t => t is AutoFilterTransformer))
                combined.Add(new AutoFilterTransformer());

            combined.AddRange(configured);
            return combined;
        }

        /// <summary>
        /// Prepends the built-in security mutation transformers to the
        /// caller-supplied set. <see cref="PolicyMutationTransformer"/> is
        /// always active so authorization-policy metadata is enforced on the
        /// create/update/delete path without explicit opt-in; it is opt-in per
        /// table via the <c>policy-*</c> metadata keys and a no-op for tables
        /// without them. A caller-supplied <see cref="PolicyMutationTransformer"/>
        /// (e.g. one configured with a non-default admin role) takes precedence.
        /// </summary>
        internal static IReadOnlyCollection<IMutationTransformer> WithBuiltInMutationTransformers(
            IReadOnlyCollection<IMutationTransformer> configured,
            IServiceProvider? services = null)
        {
            // Keep the reserved security band (priority 0-99) for the host's built-ins:
            // a consumer mutation transformer below the floor would run ahead of the
            // tenant/policy/state-machine write guards.
            ModulePriorityFloorGuard.EnsureConsumerPrioritiesRespectSecurityFloor(
                configured, t => t.Priority, "mutation");

            var combined = new List<IMutationTransformer>();

            if (!configured.Any(t => t is PolicyMutationTransformer))
                combined.Add(new PolicyMutationTransformer());

            if (!configured.Any(t => t is StateMachineMutationTransformer))
                combined.Add(new StateMachineMutationTransformer());

            if (!configured.Any(t => t is EnumValueMutationTransformer))
                combined.Add(new EnumValueMutationTransformer());

            if (!configured.Any(t => t is ExtendedServerValidationTransformer))
                combined.Add(new ExtendedServerValidationTransformer(
                    services?.GetServices<IServerValidationProvider>() ?? Array.Empty<IServerValidationProvider>()));

            // Soft delete is one feature split across two transformers: the filter
            // hides soft-deleted rows, this mutation rewrites DELETE into an UPDATE of
            // the soft-delete column. Auto-registering only the filter (see
            // WithBuiltInFilterTransformers) would leave DELETEs hard — incoherent. A
            // no-op for tables without the soft-delete metadata key, so always safe.
            if (!configured.Any(t => t is SoftDeleteMutationTransformer))
                combined.Add(new SoftDeleteMutationTransformer());

            // Tenant isolation, like soft delete, is one feature split across a
            // read-side filter and a write-side mutation transformer. Auto-
            // registering only TenantFilterTransformer (see
            // WithBuiltInFilterTransformers) would guard reads while leaving
            // UPDATE/DELETE/INSERT able to cross tenant boundaries — a one-
            // directional guarantee is no guarantee. A no-op for tables without
            // the tenant-filter metadata key, so always safe to auto-register.
            if (!configured.Any(t => t is TenantMutationTransformer))
                combined.Add(new TenantMutationTransformer());

            // Audit-column population (created/updated/deleted on/by). Keys off
            // per-column "populate" metadata plus the model-level user-audit-key, so
            // it is a no-op for tables without audit columns and always safe to
            // auto-register. A caller-supplied instance takes precedence.
            if (!configured.Any(t => t is AuditMutationTransformer))
                combined.Add(new AuditMutationTransformer());

            // Optimistic concurrency: rejects a stale UPDATE whose version token no
            // longer matches. Keys off the concurrency-token metadata, so it is a
            // no-op for tables without it and always safe to auto-register.
            if (!configured.Any(t => t is ConcurrencyMutationTransformer))
                combined.Add(new ConcurrencyMutationTransformer());

            combined.AddRange(configured);
            return combined;
        }
    }
}
