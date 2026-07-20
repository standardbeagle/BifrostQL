using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// The approval write-gate (Approval slice 2). On a table that opts into approval
    /// (<c>ApprovalConfig.FromTable(table).RequiresApproval</c>), this before-commit hook
    /// serializes the intended change into a <c>pending_changes</c> row (state
    /// <c>pending</c>) and DIVERTS the mutation: the target write is skipped and the caller
    /// is told the change is pending approval, so the data change never lands — only the
    /// pending row does. An ungated table is a pure no-op (one metadata read), so a
    /// non-approval host pays nothing.
    ///
    /// <para><b>Divert, not veto — why.</b> The pending row is written on the mutation's OWN
    /// <see cref="MutationObserverContext.Connection"/>/<see cref="MutationObserverContext.Transaction"/>
    /// so that on SQLite (a single-writer engine whose outer transaction already holds a
    /// reserved lock) a second connection would deadlock. But a before-commit VETO surfaces as
    /// a <c>BifrostExecutionError</c> that ROLLS the mutation's transaction back — which would
    /// discard the pending row written on it. So instead of returning a veto error, the hook
    /// records a DIVERT signal in <see cref="MutationObserverContext.MutationState"/> (via
    /// <see cref="TryGetDivertMessage"/>) and returns no error. Each write path, after running
    /// the before-commit hooks, checks that signal: it SKIPS the target write, lets the
    /// transaction COMMIT (so the pending row lands), and only then surfaces the
    /// pending-approval error to the caller. Net effect on a gated write: ZERO target rows
    /// changed, EXACTLY ONE pending row committed.</para>
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
    /// model, or whose enqueue INSERT fails, throws — the mutation aborts with nothing written.
    /// There is no path on which a gated table's write reaches the target table un-enqueued.</para>
    /// </summary>
    public sealed class ApprovalInterceptMutationHook : IBeforeCommitMutationHook
    {
        /// <summary>Error code carried by the pending-approval result, so transport gates can map it.</summary>
        public const string PendingApprovalCode = "PENDING_APPROVAL";

        // The divert signal + the message the pipeline surfaces once the enqueue is committed.
        // Stored in the mutation's shared MutationState bag (ordinal key, code-owned).
        internal const string DivertMessageKey = "bifrost.approval.pending-message";

        /// <summary>Internal marker used only when an already-approved intent is replayed.</summary>
        internal const string ReplayMarkerKey = "bifrost.approval.replay";

        /// <summary>
        /// True when the before-commit phase DIVERTED this mutation into approval (the hook
        /// enqueued a pending_changes row on the mutation's transaction). Every write path calls
        /// this AFTER the before-commit hooks and BEFORE the target write: when it returns true
        /// the path must SKIP the write, let the transaction commit so the pending row lands, and
        /// then surface <paramref name="message"/> to the caller via <see cref="PendingApprovalError"/>.
        /// </summary>
        public static bool TryGetDivertMessage(MutationObserverContext context, out string message)
        {
            if (context.MutationState.TryGetValue(DivertMessageKey, out var value) && value is string s)
            {
                message = s;
                return true;
            }

            message = string.Empty;
            return false;
        }

        /// <summary>The pending-approval error a write path throws (after committing the enqueue).</summary>
        public static BifrostExecutionError PendingApprovalError(string message)
            => new(message) { ErrorCode = PendingApprovalCode };

        public async ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            var config = ApprovalConfig.FromTable(context.Table);
            if (!config.RequiresApproval ||
                (context.UserContext.TryGetValue(ReplayMarkerKey, out var replay) && replay is true))
                return Array.Empty<string>(); // Ungated or approved replay: proceed normally.

            // Fail-closed: the gate can only run in the before-commit phase, where these are
            // populated on every write path. Their absence means the hook was invoked outside
            // that phase — refuse rather than let a gated write slip through un-enqueued.
            if (context.Connection is null || context.Dialect is null || context.Model is null)
                throw new BifrostExecutionError(
                    $"The approval gate for '{Qualify(context.Table)}' was invoked without an open " +
                    "transaction; the change could not be enqueued and the write is refused.");

            var store = ModelTableReference.Find(context.Model, PendingChangeStore.TableName)
                ?? throw new BifrostExecutionError(
                    $"Table '{Qualify(context.Table)}' is approval-gated but the pending-changes store " +
                    $"table '{PendingChangeStore.TableName}' is not present in the model; the write cannot " +
                    "be enqueued and is refused.");

            var pendingRow = BuildPendingRow(context);

            // Write the pending row on the mutation's OWN transaction (see the class remarks):
            // it commits with the transaction the diverting path lets through. BuildInsertInto
            // escapes every column via the dialect, so the reserved words `table` and `state`
            // are quoted — never emitted bare. context.Transaction is null on the TreeSync path
            // (SQL-level transaction), and ExecuteNonQuery then runs on the ambient transaction.
            var tableRef = context.Dialect.TableReference(store.TableSchema, store.DbName);
            var sql = MutationCommandExecutor.BuildInsertInto(
                context.Dialect, store, tableRef, pendingRow.Keys) + ";";
            await MutationCommandExecutor.ExecuteNonQuery(
                context.Connection, context.Transaction, sql, pendingRow);

            // Signal the divert (NOT a veto — that would roll the enqueue back). The path skips
            // the target write, commits, and surfaces this message as a pending-approval error.
            context.MutationState[DivertMessageKey] =
                $"Change to '{Qualify(context.Table)}' requires approval: it was submitted as a pending " +
                $"change (state '{PendingChangeStore.StatePending}') and is pending approval — it is not " +
                "applied until approved.";
            return Array.Empty<string>();
        }

        // The pending_changes row for this mutation. `id` is an identity column, so it is
        // omitted from the INSERT; approver/decided_at/reason are null until a decision, so
        // they are omitted too (nullable). The remaining columns are keyed by the slice-1
        // store constants — never string literals — so the contract cannot drift.
        private static Dictionary<string, object?> BuildPendingRow(MutationObserverContext context)
            => new(StringComparer.OrdinalIgnoreCase)
            {
                [PendingChangeStore.ColTable] = Qualify(context.Table),
                [PendingChangeStore.ColOp] = context.MutationType.ToString().ToLowerInvariant(),
                // The POST-transformer, scoped intent — see the ordering note on the class.
                [PendingChangeStore.ColPayload] = JsonSerializer.Serialize(context.Data),
                // Requester + tenant come from the caller's context so replay (slice 3) runs
                // under the REQUESTER's scope, not the approver's.
                [PendingChangeStore.ColRequester] =
                    AuditMutationTransformer.ResolveActor(context.Model!, context.UserContext)?.ToString(),
                [PendingChangeStore.ColTenant] = ResolveTenant(context)?.ToString(),
                [PendingChangeStore.ColState] = PendingChangeStore.StatePending,
            };

        // The requester's tenant, read under the model's configured tenant-context key (the
        // same key the tenant transformer scopes writes by). Null when the table/host is not
        // tenant-partitioned — a nullable column on the store.
        private static object? ResolveTenant(MutationObserverContext context)
        {
            var key = TenantFilterTransformer.ResolveTenantContextKey(context.Model!);
            return context.UserContext.TryGetValue(key, out var value) ? value : null;
        }

        private static string Qualify(IDbTable table) => $"{table.TableSchema}.{table.DbName}";
    }
}
