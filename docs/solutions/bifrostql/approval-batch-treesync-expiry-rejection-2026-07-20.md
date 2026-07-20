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

1. Approval interception is diversion, not a transaction veto. Batch actions each enqueue and apply no target write. TreeSync must pin its mixed-tree contract: an ungated node may commit while a gated node is diverted and queued. Do not promise whole-tree atomic approval semantics unless the whole tree is gated or a separate orchestration policy supplies it.
2. Expiry is a terminal state-machine transition (`pending -> expired`), not an ad-hoc timestamp check in approval. Sweep/evaluate expiry through the state-machine/mutation seam so a later approval sees the authoritative non-pending state and cannot replay.
3. Reject/expire state changes use `IMutationIntentExecutor`/`TableMutationPipeline`, not direct SQL. Verify success with `MutationIntentResult.AffectedRows == 1`; never infer affected-row count from `.Value` or nullable success. The pipeline preserves policy, tenant, audit, and other mutation transformers.

## What did not work

- The initial TreeSync assumption treated a queued child like a veto and risked claiming all-or-nothing semantics; the proving mixed-tree test showed partial application is the shipped contract.
- Expiry was not represented as a state transition until this slice, leaving approval-time timestamp logic as an easy bypass point.
- Review caught rejection using direct `UPDATE` SQL. The first implementation test passed, but it bypassed the mutation pipeline; rework routed it through the executor and added an intent/AffectedRows contract test.

## Why it recurs

Hooks run at different pipeline boundaries, and TreeSync contains several actions in one transaction. Queue diversion can therefore differ from veto semantics. Pending rows are durable state, while wall-clock expiry is only an input to a transition. Direct SQL looks simple for administrative state changes but silently skips the same authorization/audit/scoping contract required for every mutation.

## Apply when

Adding approval, staging, batch, TreeSync, expiry sweeps, reject/cancel/timeout decisions, or any administrative mutation of queued state.

## Prevention

- Document and test mixed gated/ungated trees, including which rows commit and which are queued.
- Model every terminal decision as an allowed state-machine transition; make approval require `state == pending` after the transition path has run.
- Route every queue-state write through `IMutationIntentExecutor`; assert the exact intent, caller context, and `AffectedRows == 1`, including zero/null outcomes.
