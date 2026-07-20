---
title: Approval workflows
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

## Maker-checker versus deferred HITL

**Maker-checker** is a separation-of-duties policy: an approver must be a
qualified checker and may be prohibited from approving their own request
(`self-approve: false`). It controls who may make a decision.

**Deferred human-in-the-loop (HITL)** is the execution model: the requester
submits an intent now, a person decides later, and the accepted intent is
replayed only after that decision. Maker-checker can use deferred HITL, but the
two are not synonyms: deferred HITL does not itself require a different person,
and maker-checker is the policy that does.

## Lifecycle

`pending_changes` uses the existing state-machine metadata. Its only legal
transitions are `pending → approved`, `pending → rejected`, and `pending →
expired`; all three terminal states have no outgoing transition. This prevents a
terminal change from being decided again. Decision and replay endpoints,
including expiry scheduling, are not exposed by the current interception slice;
do not treat an application-side timestamp check as an approval guarantee.
