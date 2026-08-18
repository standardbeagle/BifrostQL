---
title: "Maker-Checker Approval for Row Edits"
description: "Divert risky writes into a pending queue, then approve or reject each one through GraphQL, replaying the approved change through the pipeline as its requester."
---

Maker-checker approval holds a risky write outside its target table until a second person
accepts it. An approval-gated table declares metadata such as:

```text
main.orders { approval: enabled; approver-role: manager; self-approve: false }
```

A gated write is diverted before its target SQL runs. BifrostQL stores the
post-transformer intent in `pending_changes` with state `pending`, commits that
row, and reports `PENDING_APPROVAL` to the requester. The target row is not
inserted, updated, or deleted. The queued payload is already tenant-scoped and
captures the requester, the tenant, and the requester's full user context, so
approval replays the write as that requester.

`approver-role` is required once `approval: enabled` is present; model loading rejects a
table that declares one without the other. `self-approve` defaults to `true`, so a
deployment that wants separation of duties must set it to `false` explicitly.

## Deciding a pending change

Two mutations appear on the schema as soon as any table is approval-gated:

```graphql
mutation {
  approve(pendingChangeId: 17)
}

mutation {
  reject(pendingChangeId: 17, reason: "Discount exceeds the desk limit")
}
```

Both return `Boolean!`. A rejection reason is required and a blank one is refused — the
reason is the audit record of why the write never happened.

Approval reloads the stored payload and routes one intent through
`IMutationIntentExecutor` under **the requester's** captured user context. Every
transformer therefore sees the write it would have seen originally: tenant scoping, row
scope, soft delete, field encryption, and state-machine checks all apply to the replay.
The approver's identity is carried separately as the audit actor, so the audit trail
records who let the change through while the data lands under the requester's scope.

For a delete, the replay clears the stored value map and re-runs the intent, letting the
pipeline decide hard versus soft delete from the table's own metadata.

The data write and the `pending → approved` stamp share one transaction. The stamp is
conditional on the row still being `pending`, so a change decided concurrently rolls the
replayed write back rather than applying twice.

### Who may decide

Every decision runs the same authorization, in this order:

1. The change must still be `pending`. A decided change is refused.
2. When `self-approve: false`, the requester may not decide their own change.
3. The caller must hold the table's `approver-role`.
4. The caller must pass the table's policy for the action being approved.

Rejection runs the identical check set, so a caller who cannot approve a change cannot
reject it either.

## Expiry

`ExpireAsync` moves a pending change to `expired` through the mutation pipeline and the
state machine, and records the reason. It is a seam for a host-supplied scheduler:
BifrostQL ships no expiry sweeper and no expiry-window metadata key, and approval never
infers expiry from a timestamp. A deployment that needs pending changes to lapse must call
it on its own schedule.

## Batch and TreeSync behavior

Each action in a gated batch creates its own pending change; none of the batch's
target writes apply.

TreeSync evaluates each node independently. A gated node is queued and is not
written. Ungated nodes already processed in the same TreeSync transaction still
commit. Therefore a mixed gated/ungated tree has **partial application**: an
ungated parent can be written while its gated child remains pending. Use an
entirely gated tree when a workflow requires all nodes to wait for approval.

## Maker-checker approval versus Deferred-Effects HITL

**Maker-checker approval** is this feature's model: the write is queued in
`pending_changes`, not applied to its target table, until an authorized checker
approves it. `self-approve: false` adds separation of duties by preventing the
requester from being that checker.

**Deferred-Effects human-in-the-loop (HITL)** is different: the write is live
immediately, but a downstream effect (for example propagation, delivery, or an
external action) is held for human confirmation. Do not use approval-gated tables
when the data itself must be live while only its effects are deferred.

## Lifecycle

`pending_changes` uses the existing state-machine metadata. Its only legal
transitions are `pending → approved`, `pending → rejected`, and `pending →
expired`; all three terminal states have no outgoing transition. This prevents a
terminal change from being decided again.

The store's columns are `id`, `table`, `op`, `intended_payload`, `requester`, `tenant`,
`requester_context`, `state`, `approver`, `decided_at`, and `reason`. A gated write is
refused when the store table is absent, so a half-configured deployment fails closed
rather than writing unreviewed rows.

## Related

- [Deferred and Reversible Change Sets](/BifrostQL/guides/deferred-effects/) — the
  live-data alternative described above.
- [Metadata-Defined State Machines](/BifrostQL/guides/state-machines/) — the lifecycle
  engine `pending_changes` reuses.
- [Authorization Policies](/BifrostQL/guides/authorization/) — the policy evaluated on
  every decision.
