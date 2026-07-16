---
written_at: 2026-07-16T22:03:37Z
source_event: task:01KXM3YXQDSA2ZMXMZTB1Q2X3T
module: bifrostql
category: logic-errors
confidence: high
sources:
  - task:01KXM3YXQDSA2ZMXMZTB1Q2X3T
  - git:c868174
  - git:3694b90
  - git:1324859
tags: [protocol-adapter, storage, compensating-transaction, mutation-pipeline, rewind, review-technique, fixture-diversity]
status: steering
recurrence: 1
---

# S3 slice 1: two fail-opens a 4900-test green suite missed (1 rewind)

**Lesson.** A 4900-test green Core suite passed on attempt 1; a security
review rewind still found two real fail-opens by reasoning about the code,
not by running more tests. Both bugs shared a root cause (conflating an
*address* with a *storage key*) and both were invisible to the suite because
every seam test used the same single-column `id=1` fixture with no
pre-existing object at that key.

**What didn't work (attempt 1).**

1. `FileObjectSeam.PutAsync` checked only READ visibility before uploading,
   then ran the mutation pipeline (the real write gate) afterward. Because
   the upload target was the caller-supplied, deterministic object *address*
   (`S3ObjectKeyMap`) used directly as the *storage* key, a read-visible but
   write-denied caller overwrote the victim's blob in place — and the
   subsequent compensating delete then removed it, leaving the row pointer
   aimed at nothing. `FileUploadResolver` already carried both protections
   (pipeline pre-check before upload, random storage key) in the same repo;
   the new seam did not consult it and reintroduced the gap from scratch.
2. The TOCTOU guard in `UpdatePointerAsync` read
   `MutationIntentResult.Value` and compared it to `0` to detect a
   scoped-away write. `TableMutationPipeline.UpdateAsync` returns
   `keyData.Count == 1 ? keyData.Values.First() : result` — for a
   single-column PK, `Value` **is the key**, not an affected-row count. The
   guard was inert for every nonzero single-key row (a scoped-away write
   reported success, stranding the blob — the exact failure the guard's own
   comment claimed to prevent) and misfired on PK value `0` (a legitimate
   write treated as failed, then destructively "compensated"). Every seam
   test used `id=1`, so the composite-PK branch (which returns a genuine
   count) was exercised but this one never was, in either direction.
   Two existing tests actively encoded the bugs as correct: one asserted
   `File.Exists(bucketDir/file_data/1)` (the storage key *was* the address),
   another asserted in-place overwrite as the expected outcome.

**Why it recurs.** Any write-with-compensation design built around a
deterministic identity is one accidental reuse away from turning "roll back
my failed write" into "destroy whatever was already there." And any code
that reads a shared pipeline's return value without checking its documented
contract will silently do the wrong thing on the one input shape (single-key
PK, zero-valued key) that the happy-path fixture never produces.

**Apply when.**
- Any storage/content-addressed write path that has a compensating
  rollback/delete on failure.
- Any caller of `TableMutationPipeline.UpdateAsync` /
  `MutationIntentExecutor` that wants an affected-row signal rather than the
  GraphQL-compatible `Value` (PK for single-key tables, count for composite)
  — use `MutationIntentResult.AffectedRows`, added in this slice
  specifically because `Value` cannot answer that question. The review
  flagged RESP's `SET`/`HSET` write commands as discarding the executor
  result entirely (only `DELETE` reads `Value`, and only because it happens
  to equal the delete count) — an open follow-up, not yet fixed, worth
  checking before trusting any adapter's write-success signal.
- Writing seam/resolver tests: a fixture of a single-column PK `id=1` with
  no pre-existing state cannot exercise scoped-away-writes, PK-is-zero, or
  destroy-existing-content cases. Vary PK shape (composite + single-column +
  zero-valued) and prior state (row already holds content) deliberately.

**Prevention.**
1. Keep the address (deterministic, used for lookup/routing) and the
   storage key (the actual write target) as two different values whenever a
   compensating path exists; a fresh random storage key makes destruction of
   pre-existing content structurally unreachable rather than merely
   unlikely.
2. Before reading any pipeline/executor return value as a count, check its
   contract at the source (doc comment or implementation), not the
   happy-path test output.
3. Before building a new write seam, grep for the nearest existing resolver
   solving the same problem (`FileUploadResolver` for file writes) and diff
   against it; don't rediscover its protections the hard way.
4. When verifying a rework's claimed fix, revert it in the working tree and
   reproduce the originally reported failure, then confirm the changed test
   assertions fail against the reverted code. Attempt 2's review did exactly
   this instead of trusting the rework's narration, and it is what makes
   "these were corrections, not weakened tests" a checked fact instead of a
   claim.

## Rejected as not durable

- "TOCTOU guard reads a raw int as a count" as a one-off bug report — kept
  instead as the general contract-reading lesson above (durable: this
  return-value shape is baked into `TableMutationPipeline.UpdateAsync` and
  will bite the next caller too).
- Invariant-7 language ("delete routes a Delete intent") does not transfer
  to this seam as originally scoped: an S3 object is a column value on a
  row, not the row itself, so Update-to-NULL is the correct mutation intent,
  not Delete. The spirit (exclusively through `IMutationIntentExecutor`,
  adapter builds no predicate) still holds and was preserved.
