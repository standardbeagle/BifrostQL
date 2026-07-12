using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The before-commit veto and post-commit notification facade split out of
    /// <see cref="DbTableMutateResolver"/>. Groups the four DI-resolved lifecycle
    /// hooks (before-commit veto, mutation observers, state-transition observers) and
    /// the workflow-trigger suppression gate that guards them, so the resolver's
    /// per-verb methods read as orchestration rather than plumbing.
    /// </summary>
    internal static class MutationNotifier
    {
        /// <summary>
        /// Runs the before-commit veto phase immediately before the write (inside the
        /// transaction). If any hook returns errors (or throws), the aggregated errors
        /// surface as a <see cref="BifrostExecutionError"/> so the caller's write is
        /// aborted and nothing is committed. The result is not known yet (null).
        ///
        /// NOT gated by the workflow-trigger suppression flag, for the same reason the
        /// after-write phase is not: these two phases are the two halves of ONE mutation's
        /// in-transaction observation (the change-history writer reads the before-image in
        /// this phase and records it in the next), so suppressing one and not the other
        /// would leave a workflow-triggered write with no before-image and roll it back.
        /// Suppression exists to stop a workflow trigger from re-firing itself — that
        /// recursion is cut where it happens, in the post-commit observer phase
        /// (<see cref="NotifyMutationAsync"/>, which hosts <c>WorkflowTriggerHost</c>).
        /// Skipping veto hooks here additionally meant a workflow-triggered write bypassed
        /// every veto a normal write must pass — a fail-open. A host hook that genuinely
        /// must stand down inside a workflow can check
        /// <see cref="IsWorkflowTriggerSuppressed"/> on its own user context.
        /// </summary>
        public static async ValueTask RunBeforeCommitHooksAsync(
            IServiceProvider? services,
            IDbTable table,
            MutationType mutationType,
            IDictionary<string, object?> data,
            IDictionary<string, object?> userContext,
            DbConnection connection,
            // Null on the TreeSync path, which drives its transaction with SQL BEGIN/COMMIT
            // on the connection rather than through a DbTransaction object; a hook's own
            // command then runs on that same ambient transaction. The connection's presence
            // — not the transaction object's — is what tells a hook it is in-transaction.
            DbTransaction? transaction,
            IDbModel model,
            ISqlDialect dialect,
            IDictionary<string, object?> mutationState)
        {
            if (services is null)
                return;

            var hooks = services.GetService<BeforeCommitMutationHooks>();
            if (hooks is null)
                return;

            var errors = await hooks.RunAsync(new MutationObserverContext
            {
                Table = table,
                MutationType = mutationType,
                Data = data,
                Result = null,
                UserContext = userContext,
                // A before-commit hook may write into the SAME transaction (outbox /
                // domain events), so it receives the open connection/transaction plus
                // the model and dialect it needs to build that write.
                Connection = connection,
                Transaction = transaction,
                Model = model,
                Dialect = dialect,
                // Shared with this mutation's after-write phase: a hook that reads the
                // pre-write row (the history before-image) leaves it here.
                MutationState = mutationState,
            });

            if (errors.Count > 0)
                throw new BifrostExecutionError(string.Join("; ", errors));
        }

        /// <summary>
        /// Runs the after-write in-transaction phase: immediately after the data write
        /// but before commit, with the write <paramref name="result"/> available (the
        /// generated identity on an INSERT). A hook throw is NOT caught — it rolls the
        /// transaction back so the event and its data change commit or fail as a unit.
        /// Not gated by the workflow-trigger suppression flag: CDC captures every
        /// committed data change regardless of origin, and the outbox writer cannot
        /// recurse into workflow triggers the way an observer can.
        /// </summary>
        public static async ValueTask RunInTransactionHooksAsync(
            IServiceProvider? services,
            IDbTable table,
            MutationType mutationType,
            IDictionary<string, object?> data,
            object? result,
            IDictionary<string, object?> userContext,
            DbConnection connection,
            DbTransaction? transaction,
            IDbModel model,
            ISqlDialect dialect,
            IDictionary<string, object?> mutationState)
        {
            if (services is null)
                return;

            var hooks = services.GetService<InTransactionMutationHooks>();
            if (hooks is null)
                return;

            await hooks.RunAsync(new MutationObserverContext
            {
                Table = table,
                MutationType = mutationType,
                Data = data,
                Result = result,
                UserContext = userContext,
                Connection = connection,
                Transaction = transaction,
                Model = model,
                Dialect = dialect,
                // The same bag the before-commit phase of THIS mutation wrote into, so a
                // hook can pair its pre-write observation with the write's result.
                MutationState = mutationState,
            });
        }

        public static async ValueTask NotifyStateTransitionAsync(
            IServiceProvider? services,
            StateTransitionInfo? transition,
            IDictionary<string, object?> userContext)
        {
            if (transition is null || services is null)
                return;

            var observers = services.GetService<StateTransitionObservers>();
            if (observers is not null)
                await observers.NotifyAsync(transition, userContext);
        }

        public static async ValueTask NotifyMutationAsync(
            IServiceProvider? services,
            IDbTable table,
            MutationType mutationType,
            IDictionary<string, object?> data,
            object? result,
            IDictionary<string, object?> userContext)
        {
            if (services is null || IsWorkflowTriggerSuppressed(userContext))
                return;

            var observers = services.GetService<MutationObservers>();
            if (observers is not null)
            {
                await observers.NotifyAsync(new MutationObserverContext
                {
                    Table = table,
                    MutationType = mutationType,
                    Data = data,
                    Result = result,
                    UserContext = userContext,
                });
            }
        }

        /// <summary>
        /// True when the user context carries the workflow-trigger suppression flag,
        /// set while a workflow-triggered mutation runs so before-commit hooks and
        /// mutation observers don't recursively re-fire workflow triggers.
        /// </summary>
        public static bool IsWorkflowTriggerSuppressed(IDictionary<string, object?> userContext)
            => userContext.TryGetValue(BifrostQL.Core.Workflows.WorkflowTriggerHost.SuppressTriggersKey, out var value)
               && value is bool suppressed
               && suppressed;
    }
}
