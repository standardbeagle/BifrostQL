---
written_at: 2026-07-20T10:00:00Z
source_event: [task:01KWXANW88X661AB4A35JA40VC, git:1d3962d, git:6cc4a18, git:2b5f2a6]
module: bifrostql
category: approval-governance
confidence: high
form: constraint
sources:
  - task:01KWXANW88X661AB4A35JA40VC#comment-01KXZCPFYKJNKW6XFD6W4M9CEY
  - task:01KWXANW88X661AB4A35JA40VC#comment-01KXZDQ65DMQTFD2QBTPA5W7FX
  - git:1d3962d
  - git:6cc4a18
  - git:2b5f2a6
status: steering
recurrence: 1
tags: [approval, batch, treesync, expiry, state-machine, mutation-pipeline, affected-rows]
---

# Approval batch/TreeSync and decision-state invariants

## Lessons

1. Approval interception is diversion, not a transaction veto. On an approval-gated table, batch actions enqueue pending changes and apply no target writes. Without an operation ID, TreeSync must pin its mixed-tree contract: an ungated node may commit while a gated node is diverted and queued. A `save` that records an operation ID instead refuses a diverted node with `OPERATION_ID_UNSUPPORTED` and rolls back the entire tree, pending enqueue, and operation-ID record. Do not promise whole-tree atomic approval semantics unless the whole tree is gated or a separate orchestration policy supplies it.
2. Expiry is a terminal queue-lifecycle transition (`pending -> expired`), not an ad-hoc timestamp check in approval. Trusted server sweepers call `ApprovalDecisionService.ExpireAsync`, which uses `PendingChangeStore.TransitionFromPendingAsync` with a `state == pending` guard, so a later approval sees the authoritative non-pending state and cannot replay.
3. Reject/expire queue-state changes use the engine-owned `PendingChangeStore.TransitionFromPendingAsync` SQL seam; the approval queue refuses writes through the mutation pipeline. `ApprovalDecisionService` requires the returned affected-row count to equal 1. Rejection passes its tenant, self-approval, role, and policy checks; expiry is trusted-server-only and performs no authorization. Approved application-table writes still use `IMutationIntentExecutor` and the shared mutation pipeline.

## What did not work

- The initial TreeSync assumption treated a queued child like a veto and risked claiming all-or-nothing semantics; the proving mixed-tree test showed partial application is the shipped contract.
- Expiry was not represented as a state transition until this slice, leaving approval-time timestamp logic as an easy bypass point.
- The earlier executor-based rejection rework is not the current queue-state contract. The approval queue now refuses mutation-pipeline writes; rejection authorizes in `ApprovalDecisionService`, then uses the engine-owned `PendingChangeStore.TransitionFromPendingAsync` SQL seam with a `state == pending` guard and requires exactly one affected row.

## Why it recurs

Hooks run at different pipeline boundaries, and TreeSync contains several actions in one transaction. Queue diversion can therefore differ from veto semantics. Pending rows are durable state, while wall-clock expiry is only an input to a transition. Direct SQL for application-table mutations bypasses the authorization/audit/scoping contract of the shared mutation pipeline. Engine-owned bookkeeping has a separate contract: approve/reject authorize in `ApprovalDecisionService`, while queue decisions use `PendingChangeStore.TransitionFromPendingAsync` with a `state == pending` guard and require exactly one affected row; expiry is a trusted-server-only transition.

## Apply when

Adding approval, staging, batch, TreeSync, expiry sweeps, reject/cancel/timeout decisions, or any administrative mutation of queued state.

## Prevention

- Document and test mixed gated/ungated trees, including which rows commit and which are queued.
- Model every terminal decision as an allowed state-machine transition; make approval require `state == pending` after the transition path has run.
- Route every queue-state write through `IMutationIntentExecutor`; assert the exact intent, caller context, and `AffectedRows == 1`, including zero/null outcomes.
