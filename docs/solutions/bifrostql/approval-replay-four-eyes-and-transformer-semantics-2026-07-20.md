---
written_at: 2026-07-20T09:12:00Z
source_event: [task:01KWXANW81411WK7MK04CF2BJ9, workflow:01KXZ1EF5E5GYBK36TMQWEAM2T, git:458c8ea, git:0b2e37b, git:9cfc0a7, git:5e1d76f]
module: bifrostql
category: approval-replay
confidence: high
form: constraint
sources:
  - task:01KWXANW81411WK7MK04CF2BJ9#comment-01KXZ1ZRYMM962REKE2NAJ9FSN
  - task:01KWXANW81411WK7MK04CF2BJ9#comment-01KXZ3F3KHQ4MW726VHQCBCNB6
  - task:01KWXANW81411WK7MK04CF2BJ9#comment-01KXZA4RVE82H830613262Z0YM
  - task:01KWXANW81411WK7MK04CF2BJ9#comment-01KXZB1G67443YN8V76RJA901G
  - git:458c8ea
  - git:0b2e37b
  - git:9cfc0a7
  - git:5e1d76f
status: steering
recurrence: 1
tags: [approval, replay, four-eyes, identity, atomicity, encryption, soft-delete]
---

# Approval replay: preserve requester scope, separate approver audit, preserve logical intent

## Lesson
An approval replay carries two identities and two representations: reconstruct the stored requester context for policy/tenant scope, carry the approver only through a trusted audit-actor channel, and replay the original logical action through the normal mutation pipeline in the same transaction as the pending-state transition.

## What did not work
- The first replay copied the approver context and changed only tenant, allowing approver privileges to affect the write.
- Reconstructing requester context without a separate audit override stamped the target row with the requester instead of the approver.
- Replaying the queued, already-transformed encrypted payload through encrypt-on-write encrypted it again; a test that decrypted twice hid the defect.
- Soft-delete interception stored a physical `Update` and replayed it as `Update`, losing the original logical `Delete` authorization and semantics.
- The initial decision service left replay and pending-state transition on separate connections, permitting an applied write with a still-pending queue row; schema fields without resolver wiring also made the advertised mutation non-executable.

## Why it recurs
Approval queues sit after some transformers and before a later replay through all transformers. Treating a serialized payload as raw input, collapsing requester and approver into one context, or treating a soft-delete rewrite as the user's action silently changes security semantics. Separate connections and schema-only mutation declarations make partial or unreachable decisions easy to miss in unit tests.

## Apply when
Implementing approval, staging, deferred mutation, retry, or any serialized intent that is transformed once before storage and transformed again on execution.

## Prevention
1. Persist enough requester `UserContext` to reconstruct tenant, claims, and roles; authorize the approver independently with the same `PolicyEvaluator` and required role. For `self-approve:false`, compare identities unconditionally.
2. Use a trusted, non-serializable approver audit override. Never replace requester context with approver context, and never let queued client data forge the override.
3. Mark queued values as already transformed (ciphertext/blind index) or explicitly normalize them before replay. Prove one normal read/decrypt returns the original plaintext and the blind index matches it.
4. Persist the original logical `Insert`/`Update`/`Delete` before physical rewrites. Use logical `Delete` for authorization and replay; let the normal pipeline decide soft-delete versus hard-delete.
5. Wire approve/reject through an authenticated resolver to `ApprovalDecisionService`. Transition pending state and replay through the owning mutation transaction; inject replay failure and assert neither side commits. Cover requester/approver separation, missing role, unevaluable policy, encryption, soft-delete, composite keys, and reject-with-reason.
