---
written_at: 2026-07-20T05:53:51Z
source_event: [task:01KWXANW7VSG5FN642S5SH8DK5, workflow:01KXYYKXMYGDYXT1GEETX915DD, git:63c613d, git:4850dd9]
module: bifrostql
category: mutation-gating
confidence: high
form: constraint
sources:
  - task:01KWXANW7VSG5FN642S5SH8DK5#comment-01KXZ0DF2BZ1ZKK23XGG0M241E
  - workflow:01KXYYKXMYGDYXT1GEETX915DD#step-01KXZ19E0YM6HSHNV81J1Q9392
  - git:63c613d
  - git:4850dd9
status: steering
recurrence: 1
tags: [approval, before-commit, transaction, sqlite, treesync, stale-seam]
---

# Approval intercept: divert in the owning transaction

## Lesson
When a before-commit gate must persist a pending decision while preventing the target write, enqueue and divert on the mutation's owning connection/transaction; do not enqueue on a second connection and then veto.

## What did not work
The initial scaffold modeled a veto and exposed `ConnFactory` for a separate enqueue connection. A veto rolls back the outer transaction, deleting an enqueue made inside it; a second SQLite connection can deadlock while the outer transaction holds the single-writer lock. The working design records a divert signal, skips target SQL on every write path, commits the pending row, then raises the pending-approval error after commit.

## Why it recurs
Approval, staging, deferred-work, and transactional outbox-like gates all need durable side effects plus suppression of the original write. A generic veto contract assumes rollback is desirable; staged approval requires the opposite. Reusing an abandoned connection seam or stale comments can reintroduce the unsafe model.

## Apply when
A pre-write hook must both record a decision/request and ensure the original mutation is not applied, especially on single-writer databases or any transaction whose lock is held by the caller.

## Prevention
Specify the transaction outcome explicitly: pending row commits, target write does not, caller receives non-success after commit. Test SQLite same-transaction persistence and all mutation funnels (single, batch, TreeSync). Remove dead connection fields and contradictory registration/XML comments when the design changes. For TreeSync, decide whole-tree atomicity separately: mixed gated and ungated nodes currently allow the ungated siblings to commit while gated nodes are diverted; do not claim all-or-nothing until the tree policy is implemented and tested.
