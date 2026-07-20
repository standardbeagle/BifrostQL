using System.Collections.Generic;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// The pending_changes store contract: the column names of the store table and its
    /// lifecycle state model. Both are pinned as CONSTANTS here (the
    /// <c>MetadataKeys.History.Column</c> discipline) so the later slices that create the
    /// store table, divert writes into it, and apply approver decisions all reference one
    /// set of names — the contract cannot drift between slices.
    ///
    /// The lifecycle (<c>pending -&gt; approved | rejected | expired</c>) is expressed through
    /// the EXISTING state-machine module (<see cref="MetadataKeys.StateMachine"/>), NOT a
    /// second hand-rolled state enum: <see cref="StateMachineMetadata"/> produces the exact
    /// table metadata a state-machine table declares, so the store's illegal transitions are
    /// rejected by <c>StateMachineMutationTransformer</c> — the machinery that already exists
    /// and is already tested. Terminal states (<c>approved</c>/<c>rejected</c>/<c>expired</c>)
    /// have no outgoing transitions, so an approved change can never be re-decided.
    /// </summary>
    public static class PendingChangeStore
    {
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
            ColId, ColTable, ColOp, ColPayload, ColRequester, ColTenant,
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
    }
}
