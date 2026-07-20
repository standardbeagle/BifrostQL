---
written_at: 2026-07-20T11:20:00Z
source_event: [task:01KWXBS64ECHED77N97VF4Q40A, git:2e14da3, git:8367893, git:3896a72, git:424aeb9]
module: bifrostql
category: transactional-mutation-safety
confidence: high
form: constraint
sources:
  - task:01KWXBS64ECHED77N97VF4Q40A#01KXZHCGETCNE548RCE9W80512
  - task:01KWXBS64ECHED77N97VF4Q40A#01KXZK33957XX8VANXD9KS94CV
  - git:8367893
  - git:3896a72
status: steering
recurrence: 1
tags: [deferred, reverse-delta, before-image, transaction, fail-closed]
---

# Deferred reverse deltas: guard the whole transaction seam

## Lesson
A deferred reverse-delta writer is a transactional mutation module, not resolver-local bookkeeping: guard its internal tables on every write path, bind active change-set state to the exact lifecycle/source/connection/transaction/model/tables, and reject any configuration that cannot supply a complete before-image.

## What did not work
- The first implementation relied on existing table protections but missed `change_sets` and `change_set_deltas` on all single, batch, and TreeSync mutation paths.
- An untyped/reusable change-set id could be reused across mutations or transaction seams; review required a typed state record with identity and binding checks.
- Update-shaped upsert logic required a before-image before deciding that the operation had actually inserted. That made a legitimate insert fail instead of recording a delete inverse.
- Unit/config coverage passed while transactional behavior was initially unproved; SQLite integration tests were required for single-row, batch, TreeSync, and rollback atomicity.
- Reusing History's before-image is unsafe when `history-columns` narrows the snapshot. Deferrable tables with narrowed history columns must fail model validation unless deferred capture independently obtains a full row.

## Why it recurs
Hooks share mutation state across pipeline variants, while internal tables and lifecycle state are ordinary model data unless explicitly protected. Upserts may be represented by an update-shaped pipeline operation, and history capture is intentionally configurable. Happy-path tests therefore miss scope confusion, insert-vs-update ambiguity, partial snapshots, and rollback divergence.

## Apply when
Adding a mutation hook, deferred/undo store, transactional outbox/history module, batch or TreeSync writer, upsert path, or any feature that reuses a before-image or writes internal tables.

## Prevention
- Add an unconditional internal-table guard before intent construction/model lookup on every resolver, batch, and TreeSync mutation path.
- Store active state as a typed record; validate lifecycle, source, connection, transaction marker, model, and resolved store tables before reuse. Treat a null transaction with a live connection as the TreeSync transaction seam, not as out-of-transaction.
- For update-shaped upserts, determine actual insert semantics before enforcing the non-insert before-image requirement; inserted rows record a delete inverse keyed by the returned/generated identity.
- Require SQLite integration tests that prove inverse operation and before-image shape for insert/update/delete/soft-delete, shared change-set behavior in batch/TreeSync, and rollback when a hook fails or before-image capture is absent.
- Validate deferrable metadata against history configuration: narrowed `history-columns` is rejected because deferred restore requires the complete pre-mutation row. If a future design permits it, add an independent full-row capture rather than silently using a partial history snapshot.
