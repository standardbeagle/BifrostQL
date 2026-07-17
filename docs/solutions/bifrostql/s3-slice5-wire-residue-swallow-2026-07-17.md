---
written_at: 2026-07-17T11:00:00Z
source_event: task:01KXM3ZBB77B9SVD40JXQ49EPP
module: bifrostql
category: security,error-handling
confidence: high
sources:
  - task:01KXM3ZBB77B9SVD40JXQ49EPP
  - git:49ef695
  - git:1c5a9de
  - git:577b842
  - git:eba219a
tags: [protocol-adapter, s3, error-handling, residue, wire-catch, non-enumeration, review-technique]
status: steering
recurrence: 1
---

# S3 slice 5: PutObject/DeleteObject write path (1 rework, review pass on rework)

**Lesson.** Attempt 1 (49ef695 + 1c5a9de) built the write path correctly per
`.claude/rules/protocol-adapter-security.md` invariant 7 (routes exclusively
through the mutation pipeline, fail-closed `EnableWrites` gate first) but
review found one major: the wire catch folded a deliberately-thrown
operator-facing signal into the ordinary denial path. Fixed in 577b842 +
eba219a; rework reviewed clean.

## (a) A wire catch can silently defeat a seam's deliberate throw

`FileObjectSeam` threw the same `BifrostExecutionError` for four distinct
conditions: denial, scoped-away write, corrupt pointer, and post-authorization
storage RESIDUE (a compensation double-fault or a delete that fails after the
row pointer was already cleared). The S3 middleware's broad catch mapped all
four to `LogDebug` + `NoSuchKey`/`204`. For the first three that's correct
non-enumerating behavior. For residue it's exactly wrong: an operator needs to
hear about an orphaned blob at Error level, not have it vanish at a log level
nobody scrapes in production.

Root cause was a **shared base exception type carrying no signal to
distinguish "safe to swallow" from "an operator must reclaim this."** The fix
was a dedicated `FileObjectResidueException(StorageKey)` deriving from
`BifrostExecutionError` (so invariant-1 catch-filters still catch it) that the
wire catches *ahead of* the generic denial clause.

Generalizable rule: when a seam intentionally throws a distinct exception
type to carry an operator-surfacing signal (residue, corruption, invariant
violation — as opposed to "caller isn't allowed"), the wire/adapter layer
must catch that type specifically and route it to Error-level logging plus
whatever sanitized response is safe — never let a wide catch on the shared
base collapse it into the routine-denial path. This sits next to
`protocol-adapter-security.md` invariant 3 (sanitize `BifrostExecutionError`
on the wire) but is a different failure class: invariant 3 is about
*leaking* detail to the client; this is about *silently discarding* detail
that should reach the server-side operator log. Sanitize-for-client and
suppress-for-operator are not the same operation and must not share a log
statement or a log level.

## (b) Non-enumeration constrains error-code granularity, not just message content

Residue could get a distinct sanitized 500 (PUT) because it arises *only* on
the caller's own already-authorized write — a caller who reaches residue has
already passed the write gate, so a differentiated response leaks nothing. A
corrupt/unparseable pointer deliberately stays a plain `NoSuchKey`: on a
database-default-bucket deployment every column is addressable, so any
500-vs-404 split there becomes a column-type enumeration oracle. The
reusable test when adding a new error branch to a non-enumerating wire: ask
whether the condition can only be reached *after* the caller's own request
was authorized (safe to differentiate) or can be reached by probing
unauthorized targets (must stay folded into the generic denial response).

## Review technique reused (already logged at s3-slice1)

Rework's reviewer reverted the seam-side wrap in the working tree and
confirmed the new wire tests (residue -> 500/204 at Error log level) actually
fail without it — proving the assertions are load-bearing, not bent to fit.
Third recurrence of this technique on this drain; worth keeping in the
review playbook generally, not just re-noting per slice.

## Promotion note

(a) is a new error-contract failure class (wire-catch-swallows-operator-signal),
distinct from but adjacent to existing invariants 1/3/5/6/7 in
`.claude/rules/protocol-adapter-security.md`. This is its first occurrence —
recurrence-gate for promotion to that rules file is conventionally >=3 (see
slice-2 presigned-gate and additive-wiring lessons, held at steering tier for
the same reason). Recorded here as steering; flagging as a promotion
candidate if a second protocol adapter repeats the same swallow pattern.
