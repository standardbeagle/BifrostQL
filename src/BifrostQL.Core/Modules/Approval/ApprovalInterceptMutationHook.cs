using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// The approval write-gate (Approval slice 2). On a table that opts into approval
    /// (<c>ApprovalConfig.FromTable(table).RequiresApproval</c>), this before-commit hook
    /// serializes the intended change into a <c>pending_changes</c> row (state
    /// <c>pending</c>) and then VETOES the target write, so the data change never lands —
    /// only the pending row does. An ungated table is a pure no-op (one metadata read),
    /// so a non-approval host pays nothing.
    ///
    /// <para><b>Why the enqueue runs on its OWN connection.</b> The veto is expressed by
    /// returning a non-empty error list, which <c>MutationNotifier.RunBeforeCommitHooksAsync</c>
    /// turns into a <c>BifrostExecutionError</c> that rolls the mutation's transaction back.
    /// The pending row must SURVIVE that rollback, so it cannot be written on the mutation's
    /// <see cref="MutationObserverContext.Connection"/>/<see cref="MutationObserverContext.Transaction"/>
    /// — it is committed on a fresh connection from
    /// <see cref="MutationObserverContext.ConnFactory"/> before the veto is returned. The
    /// target write, which had not executed yet, is then aborted with nothing written to
    /// the target table. Net effect on a gated write: ZERO target rows changed, EXACTLY ONE
    /// pending row committed.</para>
    ///
    /// <para><b>Ordering — the security crux.</b> This hook runs in the before-commit phase,
    /// which every write path enters AFTER its mutation transformers have already shaped the
    /// intent (tenant pin, policy scope): the pipelines call
    /// <c>ctx.Transformers.TransformAsync</c>, adopt <c>transformResult.Data</c>, and only then
    /// build the hook context and run the before-commit hooks. So the payload serialized here
    /// is the SCOPED, post-transformer intent — an out-of-tenant value the client sent has
    /// already been overwritten with the caller's tenant before it reaches this hook — and a
    /// slice-3 replay can therefore never resurrect an out-of-scope write. The requester
    /// identity and tenant are captured from <see cref="MutationObserverContext.UserContext"/>
    /// at enqueue time, so replay runs under the REQUESTER's scope, not the approver's.</para>
    ///
    /// <para><b>Fail-closed.</b> A gated write whose pending_changes store is missing from the
    /// model, or whose enqueue cannot be committed, is REFUSED (the mutation aborts with an
    /// error). There is no path on which a gated table's write reaches the target table
    /// un-enqueued.</para>
    /// </summary>
    public sealed class ApprovalInterceptMutationHook : IBeforeCommitMutationHook
    {
        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            // Scaffold: real interception is implemented in the GREEN step.
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
}
