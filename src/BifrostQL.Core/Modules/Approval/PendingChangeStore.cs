using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// The pending_changes store contract: the column names of the store table and its
    /// lifecycle state model. Both are pinned as CONSTANTS here (the
    /// <c>MetadataKeys.History.Column</c> discipline) so the later slices that create the
    /// store table, divert writes into it, and apply approver decisions all reference one
    /// set of names — the contract cannot drift between slices.
    ///
    /// The lifecycle (<c>pending -&gt; approved | rejected | expired</c>) is enforced by
    /// <see cref="TransitionFromPendingAsync"/>, the one statement every decision writes
    /// through: it is guarded on the row still being <c>pending</c>, so a terminal change can
    /// never be re-decided. The store takes no write from any mutation path
    /// (<see cref="IsApprovalQueue"/>), so the state-machine transformer never sees a
    /// store write; <see cref="StateMachineMetadata"/> remains the metadata a deployment may
    /// declare on the store so its reads describe the same lifecycle.
    /// </summary>
    public static class PendingChangeStore
    {
        /// <summary>
        /// Conventional DbName of the pending-changes store table. The intercept hook (slice 2)
        /// resolves the store by this name from the model — there is no per-table metadata key
        /// pointing at it — so a deployment that opts a table into approval MUST create a table
        /// with this DbName (in any schema) carrying the <see cref="StateMachineMetadata"/>. When
        /// the store table is absent from the model, a gated write is REFUSED (fail-closed): a
        /// gate that cannot enqueue must never let the write through un-gated.
        /// </summary>
        public const string TableName = "pending_changes";

        // --- Store schema columns (id, table, op, intended-payload json, requester, tenant,
        // state, approver, decided_at, reason). Names pinned so slices 2-3 use these, never
        // string literals at call sites. ---

        /// <summary>Surrogate PK of the pending change.</summary>
        public const string ColId = "id";

        /// <summary>Qualified name of the gated table the pending write targets (e.g. <c>dbo.orders</c>).</summary>
        public const string ColTable = "table";

        /// <summary><c>insert</c>/<c>update</c>/<c>delete</c> — the intended mutation.</summary>
        public const string ColOp = "op";

        /// <summary>JSON body of the intended write, replayed verbatim when the change is approved.</summary>
        public const string ColPayload = "intended_payload";

        /// <summary>User id of the requester who submitted the pending change.</summary>
        public const string ColRequester = "requester";

        /// <summary>Tenant id captured from the requester's user context (nullable).</summary>
        public const string ColTenant = "tenant";

        /// <summary>Serialized requester identity context reconstructed for approved replay.</summary>
        public const string ColRequesterContext = "requester_context";

        /// <summary>Lifecycle state column — the state-machine <see cref="MetadataKeys.StateMachine.StateColumn"/>.</summary>
        public const string ColState = "state";

        /// <summary>User id of the approver who decided the change (nullable until decided).</summary>
        public const string ColApprover = "approver";

        /// <summary>Timestamp the change was approved/rejected/expired (nullable until decided).</summary>
        public const string ColDecidedAt = "decided_at";

        /// <summary>Free-text reason recorded with a rejection/expiry (nullable).</summary>
        public const string ColReason = "reason";

        /// <summary>Every column of the pending_changes store, in declaration order.</summary>
        public static readonly IReadOnlyList<string> Columns = new[]
        {
            ColId, ColTable, ColOp, ColPayload, ColRequester, ColTenant, ColRequesterContext,
            ColState, ColApprover, ColDecidedAt, ColReason,
        };

        // --- Lifecycle state model, expressed through the state-machine module. ---

        /// <summary>Initial state of a submitted change.</summary>
        public const string StatePending = "pending";

        /// <summary>Terminal state: the change was approved and applied.</summary>
        public const string StateApproved = "approved";

        /// <summary>Terminal state: the change was rejected.</summary>
        public const string StateRejected = "rejected";

        /// <summary>Terminal state: the change expired before a decision.</summary>
        public const string StateExpired = "expired";

        /// <summary>
        /// The comma-separated <see cref="MetadataKeys.StateMachine.States"/> value for the
        /// store: the four lifecycle states.
        /// </summary>
        public const string States = StatePending + ", " + StateApproved + ", " + StateRejected + ", " + StateExpired;

        /// <summary>
        /// The <see cref="MetadataKeys.StateMachine.Transitions"/> value for the store: the
        /// only legal moves are out of <c>pending</c>. The terminal states have no outgoing
        /// transitions, so <c>StateMachineMutationTransformer</c> rejects re-deciding a change.
        /// </summary>
        public const string Transitions =
            StatePending + "->" + StateApproved + "; " +
            StatePending + "->" + StateRejected + "; " +
            StatePending + "->" + StateExpired;

        /// <summary>
        /// Builds the state-machine table metadata that pins the pending_changes lifecycle.
        /// The later slice that creates the store table applies these keys to it so the EXISTING
        /// <c>StateMachineMutationTransformer</c> enforces the lifecycle — no second state enum.
        /// </summary>
        public static IDictionary<string, object?> StateMachineMetadata() =>
            new Dictionary<string, object?>
            {
                [MetadataKeys.StateMachine.StateColumn] = ColState,
                [MetadataKeys.StateMachine.InitialState] = StatePending,
                [MetadataKeys.StateMachine.States] = States,
                [MetadataKeys.StateMachine.Transitions] = Transitions,
            };

        /// <summary>
        /// The store table the approval module writes: the model table named
        /// <see cref="TableName"/> (any schema, first match — the one rule the intercept hook,
        /// the decision service and the write guard share), or null when the model has none.
        /// </summary>
        internal static IDbTable? Find(IDbModel model) => ModelTableReference.Find(model, TableName);

        /// <summary>
        /// True when <paramref name="table"/> is the approval queue of a model that gates at
        /// least one table. Such a table is written ONLY by the approval module (the enqueue in
        /// <see cref="ApprovalInterceptMutationHook"/>, the decisions in
        /// <see cref="ApprovalDecisionService"/>), never through a mutation path: a caller who
        /// could write it could forge a change under any requester identity, or mark one
        /// approved, without the decision's tenant, self-approval, role and policy checks.
        /// <c>TableMutationPipeline.GuardNotHistoryTarget</c> refuses every such write, and
        /// <see cref="FilterTransformersWrap"/> scopes every read to the caller's tenant
        /// (<see cref="TenantScopeFilter"/>). A same-named table in a model with no approval
        /// gate is an ordinary application table and is left alone.
        /// </summary>
        internal static bool IsApprovalQueue(IDbModel model, IDbTable table)
            => string.Equals(table.DbName, TableName, StringComparison.OrdinalIgnoreCase)
               && Find(model) is { } store
               // Compared by name, not instance: a table object from another load of the same
               // model must not slip past the guard.
               && string.Equals(store.TableSchema, table.TableSchema, StringComparison.OrdinalIgnoreCase)
               && model.Tables.Any(candidate => ApprovalConfig.FromTable(candidate).RequiresApproval);

        /// <summary>
        /// The row scope of every read of the queue: changes queued in the caller's tenant (the
        /// claim under the model's tenant-context key, compared in the string form the gate
        /// stores), or, for a caller with no tenant claim, changes queued without one. The
        /// queue holds each change's intended payload and requester identity, so a read is
        /// scoped exactly as a decision is (<see cref="ApprovalDecisionService"/>).
        /// </summary>
        internal static TableFilter TenantScopeFilter(IDbModel model, IDbTable store, IDictionary<string, object?> userContext)
        {
            var tenantKey = TenantFilterTransformer.ResolveTenantContextKey(model);
            var tenant = userContext.TryGetValue(tenantKey, out var value) ? value?.ToString() : null;
            return tenant is null
                ? TableFilterFactory.IsNull(store, ColTenant)
                : TableFilterFactory.Equals(store, ColTenant, tenant);
        }

        /// <summary>The refusal every mutation path returns for a write to the store.</summary>
        internal const string DirectWriteRefusal =
            "Access denied: the approval queue is written only by approve and reject.";

        /// <summary>
        /// Moves one change out of <see cref="StatePending"/> into <paramref name="toState"/>,
        /// recording the decider, the decision time and the reason. The UPDATE is guarded on
        /// the row still being pending — the only legal source state — so a change decided
        /// concurrently, or already terminal, matches nothing. Returns the affected-row count;
        /// the caller refuses anything but 1. Runs on the caller's connection and transaction,
        /// so the approval stamp commits atomically with the replayed write.
        /// </summary>
        internal static async ValueTask<int> TransitionFromPendingAsync(
            DbConnection connection, DbTransaction? transaction, ISqlDialect dialect, IDbTable store,
            object pendingChangeId, string toState, string? decider, string? reason,
            CancellationToken cancellationToken = default)
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [ColState] = toState,
                [ColApprover] = decider,
                [ColDecidedAt] = DateTimeOffset.UtcNow,
                [ColReason] = reason,
                [ColId] = pendingChangeId,
                ["from_state"] = StatePending,
            };
            var sql =
                $"UPDATE {dialect.TableReference(store.TableSchema, store.DbName)} " +
                $"SET {dialect.EscapeIdentifier(ColState)}=@state, " +
                $"{dialect.EscapeIdentifier(ColApprover)}=@approver, " +
                $"{dialect.EscapeIdentifier(ColDecidedAt)}=@decided_at, " +
                $"{dialect.EscapeIdentifier(ColReason)}=@reason " +
                $"WHERE {dialect.EscapeIdentifier(ColId)}=@id " +
                $"AND {dialect.EscapeIdentifier(ColState)}=@from_state;";
            return await MutationCommandExecutor.ExecuteNonQuery(
                connection, transaction, sql, parameters, cancellationToken: cancellationToken);
        }
    }
}
