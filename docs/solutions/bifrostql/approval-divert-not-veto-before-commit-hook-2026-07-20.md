---
written_at: 2026-07-20T06:00:00Z
source_event: task:01KWXANW7VSG5FN642S5SH8DK5
module: bifrostql-approval
category: architecture-constraint
confidence: high
form: constraint
sources:
  - git:63c613d
  - task:01KWXANW7VSG5FN642S5SH8DK5#comment-01KXZ0DF2BZ1ZKK23XGG0M241E
  - task:01KWXANW7VSG5FN642S5SH8DK5#comment-01KXZ19V6RRXPG0Q8MAHZZ1BVZ
tags: [approval, before-commit-hook, transaction, sqlite, divert, mutation-pipeline]
status: steering
recurrence: 1
---

## Lesson

A `IBeforeCommitMutationHook` that must both PREVENT a write AND DURABLY
RECORD something about it (an approval-pending row, an audit-of-denial row,
etc.) cannot use the hook's veto-return contract for that recording. Veto
throws `BifrostExecutionError`, which rolls back the mutation's transaction —
discarding anything the hook itself wrote in that same transaction. Writing
the durable record on a second connection instead deadlocks on SQLite
(single-writer; the outer mutation txn holds a `BEGIN IMMEDIATE` reserved
lock, so the second connection's write blocks until the 30s busy timeout).

## What didn't work

The task's literal shape assumed a veto-returning gate ("intercept and block
the write"). Implementing it straight would have thrown away the pending-row
enqueue on rollback, or deadlocked SQLite if the enqueue tried a fresh
connection to survive the rollback.

## Why it recurs

Any before-commit hook that needs to persist a record of its own decision
sits at the same fork: same-transaction write conflicts with veto/rollback;
separate-connection write conflicts with single-writer locking. This is
structural, not a bug in one hook — it will recur for any future gate with a
"durably record + prevent" shape (e.g. an audit-of-denial hook, a
rate-limit-ledger hook).

## Apply when

Building or reviewing any `IBeforeCommitMutationHook` (or equivalent
before-commit seam) whose job is to both stop a write from landing and leave
a durable trace of that outcome — approval gates, audit-of-denial, quota
ledgers — especially on SQLite or any other single-writer engine.

## Prevention

Use the DIVERT pattern instead of veto:
1. Hook writes its durable record (pending row, audit row, etc.) on the
   mutation's OWN shared transaction/connection — never a second connection.
2. Hook records a divert signal (in-memory mutation state), returns NO error
   — the before-commit phase completes normally.
3. Every write path checks the divert signal BEFORE performing its target
   write, SKIPS the write when diverted, and lets the transaction COMMIT (so
   only the durable record lands, zero target rows change).
4. AFTER commit, the caller-facing error (e.g. `PENDING_APPROVAL`) is thrown,
   so the API surface still reports non-success — but the transaction itself
   is not what carries that signal.

Every write path sharing the seam (single-row insert/update/soft+hard
delete, batch, tree-sync) must implement the divert check — a hook is only
as fail-closed as its least-checked write path. Prove ordering (that the
hook sees the fully-scoped, post-transformer intent) with a test that varies
a scoping input (e.g. tenant id) and asserts it lands in the recorded
payload, not just that a row was written.

Known residual gap flagged by review (not yet fixed): a multi-node
tree-sync mixing gated and ungated tables is not all-or-nothing — an
ungated sibling node can still commit alongside a gated node's diverted
enqueue. This is a partial-apply/atomicity gap, not a fail-open (the gated
node's target write never leaks) — track it if extending divert to
whole-tree semantics.
