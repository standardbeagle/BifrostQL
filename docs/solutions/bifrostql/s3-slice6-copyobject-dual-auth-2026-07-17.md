---
written_at: 2026-07-17T12:41:06Z
source_event: task:01KXM3ZE6NKJ0VQDA9TBRYT4C1
module: bifrostql
category: architecture-pattern
confidence: high
sources:
  - task:01KXM3ZE6NKJ0VQDA9TBRYT4C1
  - git:9a48b4b
  - git:676497b
tags: [protocol-adapter, storage, dual-authorization, compensating-transaction, traversal-decoding, clean-run]
status: steering
recurrence: 1
---

# S3 slice 6: CopyObject dual-auth — clean run, no rewind

**Lesson.** Same-service `CopyObject` is the epic's first read-then-write
operation. It passed review in one round (0 blockers, 1 advisory) by
composing two *already-hardened, independent* seams rather than inventing
new security logic:

1. **Dual-authorization compose pattern.** Source-read and destination-write
   are separate authorizations, and the source resolve must complete FULLY
   before any destination write starts. Implementation: resolve the source
   through the existing authorized read seam first (missing/no-permission
   source → one non-enumerating `NoSuchKey`, exactly like `GetObject`,
   destination untouched); only then call `FileObjectSeam.PutAsync` for the
   destination, which applies its own tenant/permission gate, the full
   mutation pipeline, a fresh random storage key, and veto compensation
   (invariant 8a). No new security primitive was written — the pattern is
   pure composition of the read seam + write seam that prior slices already
   proved. A caller who can read source but not write destination (or vice
   versa) copies nothing, structurally, not by an added `if`.

2. **Traversal-safety-by-structure, reaffirmed.** The copy-source header
   decoder (`S3CopySource`) rejects literal/single/double-encoded `.`/`..`
   segments before lookup, but a *triple*-encoded traversal-looking source
   slips past and resolves to `NoSuchKey` (404) instead of `InvalidArgument`
   (400) — flagged by review as an advisory, not a blocker, because the
   decoded key is never spliced into a filesystem path: `S3ObjectKeyMap.ParseKey`
   maps it to a SQL-parameterized PK + column lookup, `Unescape` rejects any
   non-`%XX` char, and the write side uses a fresh random storage key
   (`PutAsync`). This is the same "address ≠ storage key" structural
   argument from slice 1 — when the decode's failure mode is at worst a
   wrong-status-code cosmetic (404 vs 400) rather than an actual escape into
   a real filesystem/storage path, it is correctly an S3-parity nit, not a
   security gap. Worth re-deriving this argument explicitly on review rather
   than treating "decoder isn't perfectly strict" as automatically a
   blocker.

**Why durable.** Any future copy/move/rename-shaped adapter operation (this
epic or another protocol adapter) will re-encounter the same shape: two
authorizations around one operation, ordering matters, and the fix is
composition of existing seams, not new logic. Pairs with
`.claude/rules/protocol-adapter-security.md` invariants 7/8.

**Not durable (excluded from this doc).** Zero rewinds, zero blockers this
slice — no defect-class or missing-criteria pattern to harvest beyond the
compose-pattern confirmation above.
