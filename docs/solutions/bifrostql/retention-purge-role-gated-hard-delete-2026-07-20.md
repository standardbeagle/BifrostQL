---
written_at: 2026-07-20T04:10:00Z
source_event: task:01KWXANW75A4SDRKTFH9MTB77E
module: bifrostql
category: security-architecture
confidence: high
form: constraint
sources:
  - task:01KWXANW75A4SDRKTFH9MTB77E#comment-01KXYSPXAKM17PYEX6CYW0Y89H (impl notes: MutationIntent.ModuleArguments added to reach declared hard-delete route)
  - task:01KWXANW75A4SDRKTFH9MTB77E#comment-01KXYTK4VTMYSSEWNQJVRKP8GE (security-review: role gate MutationTransformerBase.cs:147-157 confirmed unconditional; ModuleArguments additive)
  - git:57f3f35 (MutationIntent.ModuleArguments)
  - git:d16bcb2 (RetentionPurgeEngine)
tags: [retention, mutation-pipeline, role-gate, synthesized-identity, background-sweep, hard-delete]
status: steering
recurrence: 1
---

# System sweeps reaching a role-gated write path: extend the intent, never the privilege

## Lesson

A background/system sweep (no caller identity) that needs to reach a
role-gated mutation route (e.g. `soft-delete-hard-role` hard-delete) must do
so by (a) extending `MutationIntent` with an explicit, narrow signal
(`ModuleArguments`, e.g. `{hard_delete: true}`) that the pipeline's existing
role gate still evaluates unconditionally, and (b) granting the synthesized
system `UserContext` **exactly** the table's declared role for that route —
never a broader/admin role "to make the sweep work." The role gate itself
must run the same unconditional check it runs for a real caller; the sweep
earns the route by being granted the declared role, not by bypassing the
check.

## What shipped (proof this holds structurally)

`RetentionPurgeEngine` (`src/BifrostQL.Core/Modules/Retention/RetentionPurgeEngine.cs`)
routes `retain` (hard-purge of already-soft-deleted rows) through a `Delete`
intent carrying `ModuleArguments={hard_delete:true}`, via
`IMutationIntentExecutor` → `TableMutationPipeline`. `ttl` (expire live rows)
routes a plain `Delete` intent and lets the pipeline decide soft-vs-hard
itself. `MutationTransformerBase`'s role gate (line ~147-157) is
**unconditional** — it does not special-case a system/background caller —
and the synthesized per-tenant system context is granted only the table's
declared `soft-delete-hard-role`, nothing wider. Security review confirmed
by reading the gate directly, not by trusting the sweep's own comments.

## Why it recurs

Any future background engine (scheduled export, compaction job, another
retention-shaped module) that needs a privileged write route will face the
same fork: bypass the gate with a "trusted internal caller" shortcut, or
extend the intent vocabulary and grant minimal privilege through the normal
gate. The shortcut is the easier-looking path and is exactly how a
background job becomes an unconditional-hard-delete backdoor.

## Apply when

Any new module (hosted service, scheduled sweep, cron-style engine) needs to
reach a mutation route that is gated by a declared role/permission, and the
caller is synthesized (no real per-request identity).

## Prevention

- Extend `MutationIntent`/`ModuleArguments` (or an equivalent narrow signal)
  rather than adding a bypass parameter to the transformer or gate.
- Grant the synthesized system identity exactly the declared role for the
  route being exercised — verify by reading the gate's condition, not by
  assuming the sweep's intent is trusted.
- Keep the gate itself unconditional; it must not know or care whether the
  caller is a background sweep vs. a real user.
- Test the disambiguation non-vacuously: prove a scoped-away/denied case
  produces zero effect (as this task's revert-and-rerun experiment did for
  tenant scoping), not just that the happy path works.

## Relation to existing invariants

This is a background-engine-specific refinement of
`.claude/rules/protocol-adapter-security.md` invariant 7 (adapter writes
route exclusively through `IMutationIntentExecutor`, adapter builds no
predicate, pipeline decides semantics). Invariant 7 covers protocol
*adapters*; this lesson covers *background/system sweeps with no caller
identity at all*, and adds the specific mechanism (extend the intent
envelope + minimal role grant to the synthesized identity) that keeps a
role-gated route reachable without weakening the gate. Distinct from
invariant 11 (identity-less scrape exposure), which is about read-side
tenant scoping for aggregation surfaces, not write-side role gates.
