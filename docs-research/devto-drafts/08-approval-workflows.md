---
title: "Maker-checker approval workflows for row edits"
published: false
description: "Divert risky writes into a pending queue with three lines of table metadata, then approve or reject each one through GraphQL — the approved write replays through the full mutation pipeline as its original requester."
tags: workflow, database, dotnet, audit
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/approval-workflows/
---

Three lines of table metadata turn every write to a table into a reviewed write:

```text
main.orders { approval: enabled; approver-role: manager; self-approve: false }
```

After that, a clerk's `UPDATE` does not touch the `orders` table. It lands in a `pending_changes`
row, the clerk gets an error saying the change is pending, and a manager either approves it — which
replays the write exactly as the clerk submitted it — or rejects it with a recorded reason. No
application code changes. The write path, the queue, the decision mutations, and the audit trail all
come from the same metadata line.

I ran everything below against a live BifrostQL host on SQLite. The transcripts are real output.

## Set up the queue

Approval needs a store table named `pending_changes`. BifrostQL does not create it for you, and a
gated write is **refused** when the store is missing from the model — a half-configured deployment
writes nothing rather than writing unreviewed rows. The columns are fixed:

```sql
CREATE TABLE pending_changes (
  id                INTEGER PRIMARY KEY,
  "table"           TEXT NOT NULL,
  op                TEXT NOT NULL,
  intended_payload  TEXT NOT NULL,
  requester         TEXT,
  tenant            TEXT,
  requester_context TEXT,
  state             TEXT NOT NULL DEFAULT 'pending',
  approver          TEXT,
  decided_at        DATETIME,
  reason            TEXT
);
```

The table's lifecycle is the ordinary state-machine module, declared as metadata rather than as a
second hand-written enum:

```text
main.pending_changes {
  state-column: state; initial-state: pending;
  states: pending, approved, rejected, expired;
  transitions: pending->approved|pending->rejected|pending->expired
}
```

Every terminal state has no outgoing transition, which is what stops an approved change from being
decided a second time. `approver-role` is required as soon as `approval: enabled` is present —
model loading rejects one without the other. `self-approve` defaults to `true`, so a deployment that
wants separation of duties has to ask for it.

## A gated write

Alice is a clerk. She tries to put a 500 discount on order 1:

```graphql
mutation { orders(update: { id: 1, customer: "Acme", amount: 1200, discount: 500 }) }
```

```json
{"errors":[{"message":"Change to 'main.orders' requires approval: it was submitted as a pending
change (state 'pending') and is pending approval — it is not applied until approved.",
"path":["orders"],"extensions":{"code":"BIFROST_EXECUTION_ERROR"}}],"data":{"orders":null}}
```

The queue and the target table after that request:

```
id|table       |op    |state  |requester
1 |main.orders |update|pending|alice

id|discount
1 |0.0
```

The discount is still zero. One pending row exists, and it names the table, the operation, and the
requester.

Two details matter here. First, the payload stored in `intended_payload` is the **post-transformer**
intent, not the raw GraphQL input — the tenant pin and the policy scope have already been applied,
so an approval can never replay a write that was out of scope when it was submitted. Second, the
pending row is written on the mutation's own connection and transaction. The gate is a diversion
rather than a veto: a veto would roll the pending row back along with the write it blocked.

## Deciding

Two mutations appear on the schema as soon as any table is gated:

```graphql
mutation { approve(pendingChangeId: 1) }
mutation { reject(pendingChangeId: 1, reason: "Discount exceeds the desk limit") }
```

Both return `Boolean!`. Every decision runs the same four checks in the same order — the change must
still be pending, `self-approve: false` blocks the requester, the caller must hold `approver-role`,
and the caller must pass the table's policy for the action being approved. Rejection runs the
identical set, so someone who cannot approve a change cannot reject it either.

Alice, promoted to manager, tries to approve her own change:

```json
{"errors":[{"message":"The requester cannot approve their own change."}],"data":null}
```

Bob, a clerk, tries:

```json
{"errors":[{"message":"The caller is not an approval-role holder."}],"data":null}
```

Bob with the `manager` role:

```json
{"data":{"approve":true}}
```

```
id|state   |approver|decided_at
1 |approved|bob     |2026-08-18 02:56:13.68+00:00

id|discount
1 |500.0
```

The write applied. Bob tries to approve it again:

```json
{"errors":[{"message":"The pending change has already been decided."}],"data":null}
```

That refusal comes from the state machine, not from a check somebody remembered to write in the
approval service.

## What approval actually replays

Approve reloads the stored payload and routes **one** mutation intent through
`IMutationIntentExecutor` under the requester's captured user context. Every transformer sees the
write it would have seen originally: tenant scoping, row scope, soft delete, field encryption, state
machine checks. The approver's identity rides along separately as the audit actor, so the trail
records who let the change through while the data lands under the requester's scope.

The data write and the `pending → approved` stamp share one transaction, and the stamp is conditional
on the row still being pending. A change decided concurrently rolls the replayed write back instead
of applying it twice.

A queued delete is replayed as a delete intent with the stored value map cleared, which lets the
pipeline decide hard versus soft delete from the table's own metadata. Alice's delete, rejected:

```json
{"data":{"reject":true}}
```

```
id|op    |state   |requester|approver|reason
1 |update|approved|alice    |bob     |
2 |delete|rejected|alice    |bob     |Discount exceeds the desk limit
```

A blank reason is refused — the reason is the record of why the write never happened. Sending
`reason: "   "` returns an `ARGUMENT` error and leaves the change pending.

## Expiry is a seam, not a feature

`ApprovalDecisionService.ExpireAsync` moves a pending change to `expired` through the mutation
pipeline and the state machine and records the reason. Nothing calls it. I grepped the whole tree:
two hits, one being the definition and one a test. There is no sweeper, no hosted service, no
`expiry-window` metadata key, and approval never infers expiry from a timestamp.

So `pending → expired` is reachable only from your own scheduler. If your compliance rule is
"unreviewed changes lapse after 72 hours", that timer is yours to build; the transition it calls is
already correct and already state-machine-checked.

## Batches and trees apply partially

Each action in a gated batch creates its own pending change, and none of the batch's target writes
apply. TreeSync evaluates each node independently, which has a sharper consequence: a gated node is
queued while ungated nodes already processed in the same TreeSync transaction still commit. A mixed
tree therefore applies partially — an ungated parent can be written while its gated child waits. Gate
the whole tree when a workflow needs all of it to wait.

## Two rough edges from the live run

Fail-closed configuration works, and it is loud. A `retain`-style typo or a bad value stops model
loading with the offending table and key named, which is the behavior you want from a gate.

One real defect showed up while grounding this article. The HTTP GraphQL path builds its user context
as a `BifrostContext`, which stashes the raw `ClaimsPrincipal` under a `user` key. The approval hook
serializes the whole user context into `pending_changes.requester_context` with
`System.Text.Json`, and a `ClaimsPrincipal` serializes into an object cycle
(`$.Claims.Subject.Claims.Subject…`). Every gated write from an **authenticated** GraphQL caller
therefore fails with a sanitized "A database error occurred" instead of enqueuing. Anonymous callers
work, because their context is a plain dictionary — which is exactly why the integration suite, which
builds synthetic dictionary contexts, is green. I reproduced the full maker-checker cycle above by
registering a host `IBifrostAuthContextFactory` that projects identity into a plain serializable
dictionary. If you are wiring approval onto an authenticated host today, that override is the
workaround until the context projection drops the principal.

## When to reach for it

Maker-checker holds the **data** back. The row does not change until a checker accepts it, which is
what you want for discounts, refunds, price changes, and anything a regulator will ask you to
evidence. When the data must be live immediately and only a downstream effect — a payout, a
notification, an external call — needs human confirmation, that is a different feature
(deferred effects), and gating the table would be the wrong tool.

Everything above is metadata plus a store table. The queue, the two mutations, the four
authorization checks, the transactional stamp, and the audit actor split come with it.
