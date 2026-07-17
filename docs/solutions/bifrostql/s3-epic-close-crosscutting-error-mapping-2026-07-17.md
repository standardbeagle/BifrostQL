---
written_at: 2026-07-17T13:40:00Z
source_event: task:01KWXCZ1QJTENB4F48AHC70JS2
module: bifrostql
category: security,error-handling,process
confidence: high
sources:
  - task:01KWXCZ1QJTENB4F48AHC70JS2
  - git:82376b7
tags: [protocol-adapter, s3, error-handling, non-enumeration, epic-close-gate, review-technique, wire-catch]
status: steering
recurrence: 1
---

# S3 epic close: a cross-cutting blocker every per-slice review missed (fixed in 82376b7)

**Lesson.** The S3 epic shipped 8 slices, each individually reviewed and
green (23/23 in the final middleware suite). The epic-CLOSE gate — a review
scoped to the whole epic, not any one slice — found a blocker no per-slice
review had: the read op classes (GetObject/HeadObject, CopyObject
source-resolve) caught only `InvalidOperationException`, while the write op
classes (PutObject/DeleteObject/CopyObject-destination) caught
`when (ex is InvalidOperationException or BifrostExecutionError)`. A corrupt
stored pointer or a table-level policy read-deny threw `BifrostExecutionError`,
which escaped the read-path catch to the generic handler: 500 InternalError +
Error-level log, instead of the seam's documented `BifrostExecutionError` ->
`NoSuchKey` (404) contract the write paths already honored. Net effect: a
read-denied caller got GetObject=500 while ListObjects=404 and every write
path=404 — an existence/authorization oracle across op classes, defeating the
epic's own non-enumeration invariant, even though no data actually leaked.

Fixed by widening both read-path catches to match the write paths (82376b7).
New tests cover the two throwing triggers directly (corrupt pointer, policy
read-deny) and assert both the status code AND the absence of an Error-level
log — the prior GetObject tests only exercised zero-rows-via-WHERE-filter, so
the throwing paths were untested (same fixture-too-simple gap as the s3-slice1
lesson).

## (a) Per-slice review is structurally blind to cross-slice invariants

Each of the 8 slices was reviewed on its own diff, against its own acceptance
criteria. Slice 4 (GetObject/HeadObject) reviewed its read-path catch in
isolation and it looked fine — `InvalidOperationException` is a real,
documented addressing fault, and slice 4's own fixtures never threw
`BifrostExecutionError`. Slice 5 (PutObject/DeleteObject) reviewed its
write-path catch in isolation and it also looked fine. Neither review had the
other slice's diff in context, so neither could see that the two catch
clauses, covering the *same seam's* documented exception contract, had
silently diverged. The divergence was only visible from an epic-wide vantage
point that could diff read-path against write-path behavior for the same
inputs.

**Generalization:** any multi-slice epic where N slices each implement one
facet of a single cross-cutting contract (here: non-enumeration across every
S3 op class) needs a dedicated epic-close gate that reviews the *seam*, not
the slice — explicitly diffing sibling op classes against each other for
consistency (same catch set, same auth check, same error mapping). This gate
paid for itself here: it single-handedly caught a blocker that 8 passing
per-slice reviews and a 4900+/23-test green suite did not.

## (b) The concrete code pattern: catch-set symmetry across op classes sharing a seam

When multiple adapter op classes (read vs write, list vs get, source vs
destination) route through the same underlying seam and that seam has a
documented exception -> wire-status contract, every op class's catch clause
for that seam must catch the identical exception set. A narrower catch on
one op class (here: read, missing `BifrostExecutionError`) doesn't just
under-handle — it creates a *differential* wire signal (500 vs 404) between
op classes for the same underlying condition, which is exactly what a
non-enumeration/anti-oracle contract exists to prevent. This is the third
distinct "wire catch clause exactness" defect on this epic's error-mapping
seam (see `.claude/rules/protocol-adapter-security.md` invariants 1 and 5,
and `docs/solutions/bifrostql/s3-slice5-wire-residue-swallow-2026-07-17.md`
for the residue-discrimination sibling) — recurring enough that it has been
promoted to invariant 9 in that rule file.

## (c) Fixture-too-simple recurrence

GetObject's existing tests exercised only zero-rows-via-WHERE-filter paths
(missing row, cross-tenant, no-object) — none of which throw. The throwing
paths (corrupt pointer, policy-deny) were simply never fixtured, so the read
path's narrower catch had no test that could expose it. Same shape as the
s3-slice1 address-vs-storage-key lesson: a fixture too simple to let the bug
manifest reads as coverage but isn't. Any seam with documented
exception-to-wire-status mapping needs at least one fixture per *distinct
throw site*, not just per happy-path row shape.

## Review technique worth reusing

The epic-close review scoped itself to the seam's contract (non-enumeration)
across ALL op classes simultaneously, rather than per-slice diffs — it asked
"does GetObject answer the same way ListObjects and PutObject do for the same
denied/corrupt input?" That question is only askable with the whole epic in
view, which is why it belongs at epic-close, not at any single slice gate.
