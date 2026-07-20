---
written_at: 2026-07-20T13:00:00Z
source_event: [task:01KXZNNS9SJ9Z0JACHFSFSMFM5, workflow:01KXZNNS9S38T0DSX4K7GQTY4A, git:160222c041b5429431369c0955650e7e02a29d36]
module: bifrostql
category: integrity
confidence: high
form: procedure
sources:
  - task:01KXZNNS9SJ9Z0JACHFSFSMFM5#content
  - task:01KXZNNS9SJ9Z0JACHFSFSMFM5#review
  - git:160222c041b5429431369c0955650e7e02a29d36
status: steering
recurrence: 1
---

# Undo delta safety contract preflight

## Lesson
Review the durable `change_set_delta` contract before implementing an undo replay engine. The delta is the safety boundary: undo must consume captured facts, never infer drift from mutation input.

## What did not work
The earlier undo-engine review exposed inverse data without a guaranteed durable concurrency token, and History coupling can be unsafe when `history-columns` is narrowed. Deferring this contract review lets the replay engine encode assumptions that are already too weak.

## Why it recurs
Mutation input is not the stored row. Database defaults, generated keys, triggers, and generated concurrency tokens may be absent or stale in the request payload. Delete/restore also needs the pre-delete token. A delta that lacks these facts cannot fail closed on later drift.

## Apply when
Before any undo, restore, compensation, or replay engine is built or changed; when a mutation hook captures before/after images; when concurrency tokens or generated values are database-owned.

## Prevention
- Define and test the delta schema as a complete safety contract first: full restore image, typed active `change_set` binding, and the correct durable token for each inverse predicate.
- Read the post-write row back by its resolved key inside the same transaction and persist that stored image; do not serialize `context.Data` as the after-image.
- Preserve the pre-delete token in the delete before-image.
- Classify update-shaped upsert inserts before null-before-image guards; record a delete inverse keyed by the generated identity and stored token.
- Reject `deferrable` plus narrowed `history-columns` at configuration time when History is reused for capture.
- Test generated/default concurrency tokens, delete/restore guards, upsert inserts, rollback atomicity, missing-contract-data fail-closed behavior, and internal deferred-table mutation guards independently of the replay engine.

This procedure is based on the task's preflight rationale, the accepted review, and commit `160222c`; no workflow rewind occurred in this run.
