---
written_at: 2026-07-20T04:40:00Z
source_event: task:01KWXANW7CV7VX8SCMXKQGMWAG
module: bifrostql
category: security-architecture
confidence: high
form: constraint
sources:
  - task:01KWXANW7CV7VX8SCMXKQGMWAG#comment-01KXYW3JCHKY07HVSDJP32GKZY (impl notes: tombstone decision + unforgeable marker)
  - task:01KWXANW7CV7VX8SCMXKQGMWAG#comment-01KXYWWF17J2M60MS853DDJ67K (security-review PASS, all 8 compliance points test-pinned, 0 blockers)
  - git:b1fb892 (HistoryErasure.cs, HistoryMutationHook.cs)
  - git:1ea63da (RetentionPurgeEngine erasure routing + dry-run)
tags: [retention, history, right-to-erasure, gdpr, audit-trail, mutation-pipeline, unforgeable-marker]
status: steering
recurrence: 1
---

# Right-to-erasure of an audited entity: tombstone the trail, don't append to it

## Lesson

An entity with an append-only audit/history trail (change-history, CDC,
outbox) is a self-reference problem under right-to-erasure: the trail rows
ARE personal data, and a purge that goes through the normal delete path
re-persists the very PII it exists to erase (the writer's default behavior
is "record a before-image of what was deleted"). The fix is not "skip
history for this delete" (that silently breaks the audit contract for every
other delete) and not "write a redaction event" (that is itself a new
history row about erasure — history-of-history, still growing).

The shipped pattern — **tombstone-the-trail** — is: on a delete flagged as
erasure, DELETE the entity's *existing* trail rows and INSERT exactly one
payload-free `op='erase'` tombstone (before/after both NULL), both as
**direct SQL on the purge's own transaction**, never re-entering the
mutation pipeline. Direct SQL is what makes it terminate: a normal pipeline
write would itself trigger the history hook again (history-of-history); raw
SQL against the trail table has no hook to trigger. Atomicity comes from
sharing the purge's transaction — the tombstone/trail-delete commits or
rolls back with the entity delete itself, never a separate step.

Paired anti-abuse control: the erasure signal is carried as a **process-
unique object reference** in the delete's `UserContext` (the channel a
background engine already controls), and the consuming hook checks it with
`ReferenceEquals`, not equality. A caller-supplied/deserialized identity
claim is a JSON scalar and can never be reference-equal to an in-process
object — so an external caller cannot forge the erasure signal to wipe its
own audit trail via a crafted request. Gated on a *physical* (hard) delete
only: a soft delete or TTL-driven expiry is not an erasure and must record
history normally.

## What shipped (proof this holds structurally)

`src/BifrostQL.Core/Modules/History/HistoryErasure.cs` defines the
process-unique `Marker` (a `static readonly object`) and the trail-purge +
tombstone SQL, dialect-validated via `ISqlDialect` (ScriptDom-checked for
SQL Server). `HistoryMutationHook.cs` checks
`ReferenceEquals(userContext[...], HistoryErasure.Marker)` before routing to
tombstone-the-trail instead of the normal before-image write.
`RetentionPurgeEngine` sets the marker only on `retain`/`ttl` deletes that
are physical (a soft-delete-eligible TTL expiry does not set it). Tests
(`RetentionErasureHistoryTests.cs`) prove: tombstone + purge is atomic with
the delete (a poisoned/rolled-back purge leaves no orphan tombstone), the
tombstone is payload-free, and the sequence terminates (exactly one `erase`
row, not a growing chain). Security review confirmed the forgery threat
model is fully closed by `ReferenceEquals` and flagged only a non-blocking
defense-in-depth advisory (narrow `Marker`'s visibility to `internal`).

## Why it recurs

Any future append-only side-channel — CDC outbox, webhook delivery log,
temporal-history read model — that sits behind a mutation pipeline hook
will face the same fork when right-to-erasure lands: the module's default
write-on-delete behavior re-captures the deleted row's data, and a naive
"write an erasure event instead" still grows the trail forever. This is
distinct from the ordinary hard-delete role-gating lesson (see
`retention-purge-role-gated-hard-delete-2026-07-20.md`, which is about
which caller/role may reach the delete route at all) — this lesson is about
what an audit/history *sink* itself must do once a privileged delete
reaches it, so it doesn't defeat the erasure it's being asked to honor.

## Apply when

A module maintains an append-only trail keyed to a mutable entity
(history, CDC, outbox, search-index change log) AND that entity can be
subject to a hard delete that must also satisfy a right-to-erasure /
retention-purge requirement.

## Prevention

- Never let a purge's delete flow through the trail writer's normal
  before-image path — it re-persists the erased data by design.
- Terminate structurally: use direct SQL against the trail table for the
  purge+tombstone, sharing the purge's own transaction, so there is no hook
  to re-trigger and no separate non-atomic step.
- Signal erasure with something a wire-deserialized value can never satisfy
  (a process-unique object reference + `ReferenceEquals`), not a string/enum
  flag equality-compared — equality-compared flags are forgeable by any
  caller who can set `UserContext` fields.
- Gate on *physical* delete only; a soft delete must keep recording history
  normally, or you silently break the audit contract for the common case.
- Test the termination property explicitly (row count stays at exactly one
  tombstone after the purge, not "no error thrown") and test atomicity via
  a poisoned/rolled-back transaction, not just the happy path.

## Relation to existing invariants

Complementary to `retention-purge-role-gated-hard-delete-2026-07-20.md`
(which governs *who* may reach the hard-delete route) and to
`.claude/rules/protocol-adapter-security.md` invariant 7 (writes route
exclusively through `IMutationIntentExecutor`, pipeline decides semantics)
— this lesson is the one deliberate, documented exception: the
trail-tombstone write is direct SQL specifically *because* re-entering the
pipeline would defeat the erasure by re-triggering the same hook. Any
future direct-SQL exception to invariant 7 should be justified the same
way (a structural termination requirement), not as a convenience shortcut.
