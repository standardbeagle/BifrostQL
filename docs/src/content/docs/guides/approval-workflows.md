---
title: "Maker-Checker Approval for Row Edits"
description: "Divert risky writes into a pending queue, then approve or reject each one through GraphQL, replaying the approved change through the pipeline as its requester."
---

# Approval workflows

An approval-gated table declares metadata such as:

```text
main.orders { approval: enabled; approver-role: manager }
```

A gated write is diverted before its target SQL runs. BifrostQL stores the
post-transformer intent in `pending_changes` with state `pending`, commits that
row, and reports `PENDING_APPROVAL` to the requester. The target row is not
inserted, updated, or deleted. The queued payload is already tenant-scoped and
captures the requester and tenant; a later approval/replay implementation must
run it as that requester, never as the approver.

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
terminal change from being decided again. Expiry is an explicit scheduler/sweeper
transition through the mutation pipeline and this state machine; approval never
uses an application-side timestamp check to bypass or infer the terminal state.
