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
The requester stays the audit actor: `created-by`/`updated-by`/`deleted-by` columns and the
change-history actor name the person who asked for the change. The approver is recorded on
the decision itself — `pending_changes.approver` and `decided_at` — and never widens the
replay.

For a delete, the replay clears the stored value map and re-runs the intent, letting the
pipeline decide hard versus soft delete from the table's own metadata.

The data write and the `pending → approved` stamp share one transaction. The stamp is
conditional on the row still being `pending`, so a change decided concurrently rolls the
replayed write back rather than applying twice.

### Who may decide

Every decision runs the same authorization, in this order:

1. The caller must be in the change's tenant: their tenant claim (under the model's
   `tenant-context-key`, default `tenant_id`) must equal the `tenant` recorded when the
   change was queued. A change from another tenant, or any tenant-scoped change when the
   caller has no tenant claim, answers "The pending change was not found." — the same
   answer as an id that does not exist, so ids cannot be used to probe other tenants. A
   change queued by a requester with no tenant claim has no tenant and skips this step.
   A row whose `tenant` disagrees with the tenant of its stored requester identity was not
   written by the gate and is refused the same way.
2. The change must still be `pending`. A decided change is refused.
3. When `self-approve: false`, the requester may not decide their own change.
4. The caller must hold the table's `approver-role`.
5. The caller must pass the table's policy for the action being approved.

Rejection runs the identical check set, so a caller who cannot approve a change cannot
reject it either.

## Expiry

`ExpireAsync` moves a pending change to `expired` with the same guarded transition every
decision uses, and records the reason. It is a seam for a host-supplied scheduler:
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

The only legal transitions are `pending → approved`, `pending → rejected`, and
`pending → expired`. Every decision records its transition with one UPDATE guarded on
the row still being `pending`, so a terminal change can never be decided again and two
concurrent decisions cannot both land.

## The queue is read-only to every API

Once any table is approval-gated, `pending_changes` takes no write from any mutation
entry point, whatever its metadata says: GraphQL insert/update/delete, batch (including
the bulk path), nested sync and save, filtered update, file mutations, and the protocol
adapters and MCP through `IMutationIntentExecutor`. Each is refused as an access denial,
by the same guard that keeps change-history and deferred-effects tables read-only. Only the gate
(which queues a change) and `approve`, `reject` and `ExpireAsync` (which decide one)
write it, so every row in the queue was queued by the gate and every decision went
through the checks above.

Reads stay published so approvers can list the queue, and every read is scoped the way a
decision is: a caller sees only changes queued in their own tenant (their tenant claim
equals the row's `tenant`), and a caller with no tenant claim sees only changes queued
without one. The scope is applied by the read filter funnel itself, on every read surface,
whatever filter transformers the host registered. Declare a read policy on the store (for
example `policy-actions: read[manager]`) to choose who inside a tenant may list it;
decisions do not need any write grant on the store. State-machine metadata on the store (see
`PendingChangeStore.StateMachineMetadata`) is optional: it describes the lifecycle to
readers, but no store write passes through the state-machine transformer.

The store's columns are `id`, `table`, `op`, `intended_payload`, `requester`, `tenant`,
`requester_context`, `state`, `approver`, `decided_at`, and `reason`. A gated write is
refused when the store table is absent, so a half-configured deployment fails closed
rather than writing unreviewed rows.

`requester_context` holds a projection of the requester's identity, not their request
context: the policy subject (`user_id`), the policy grants (roles and permissions), the tenant claim under the
model's tenant-context key, and the claim named by `user-audit-key`. These are the
fields the replay re-runs the mutation pipeline with. Other claims, and the raw
authentication principal, are never stored.

## Related

- [Deferred and Reversible Change Sets](/BifrostQL/guides/deferred-effects/) — the
  live-data alternative described above.
- [Metadata-Defined State Machines](/BifrostQL/guides/state-machines/) — the lifecycle
  engine `pending_changes` reuses.
- [Authorization Policies](/BifrostQL/guides/authorization/) — the policy evaluated on
  every decision.
