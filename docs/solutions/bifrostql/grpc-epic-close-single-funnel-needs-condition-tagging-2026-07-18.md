---
written_at: 2026-07-18T18:00:00Z
source_event: epic:01KWXCZ1QQAP2859FA47HK0G65
module: bifrostql
category: security,error-handling,architecture
confidence: high
sources:
  - epic:01KWXCZ1QQAP2859FA47HK0G65
  - commit:6fbcf0617d517f454f72f7a5d19b4605d97edb18
  - task:01KXM41E6BJTA7JRY6YPEE9RRS (gRPC slice 7 conformance derivation)
  - doc:docs/solutions/bifrostql/odata-epic-close-single-funnel-error-mapping-2026-07-18.md
tags: [protocol-adapter, grpc, error-handling, non-enumeration, epic-close-gate, architecture-pattern]
status: steering
recurrence: 1
---

# gRPC epic close: a single error-mapping funnel is necessary but not sufficient — condition tagging at throw sites must match too

**Lesson.** The gRPC epic (8 slices, dynamic dispatch over `IServiceMethodProvider`,
all op classes routed through one `GrpcStatusMapper` funnel since slice 3) still
produced a cross-op status divergence: a missing-tenant/policy-denied condition
surfaced `PERMISSION_DENIED` on read (List/Stream) but generic `INTERNAL` on
write (Insert/Update/Delete). This is the exact oracle-adjacent shape the S3
epic-close and OData single-funnel docs warn about — yet gRPC already HAD the
single funnel those docs prescribe as the fix. The funnel did not prevent it.

**Root cause (commit 6fbcf06).** Not a forked catch clause. The read-side
`TenantFilterTransformer` tags its fail-closed throw with
`BifrostExecutionError.AccessDeniedCode`; the write-side
`TenantMutationTransformer` threw a bare, codeless `BifrostExecutionError` for
the identical condition. `GrpcStatusMapper`'s single switch matches on that
code — no code, no match, falls to the default `INTERNAL` arm. One funnel,
two different signals arriving at it for the same condition.

**Generalization (extends, does not duplicate, the OData single-funnel doc).**
"One dispatch funnel" collapses N *catch sites* into one, which is what that
doc proves. It says nothing about the *signal* the funnel switches on — and
that signal is produced by throw sites that live in a completely different
layer (Core transformers), often written by different slices/people at
different times. Parity requires BOTH:
1. one funnel (prevents catch-site drift — already validated), AND
2. every op class's upstream throw sites tag the same underlying condition
   with the same signal (here, `AccessDeniedCode`) — the funnel can only be
   as consistent as what it's given to switch on.

A funnel is a necessary structural control, not a sufficient one. Review of a
single-funnel adapter must still check throw-site signal parity across op
classes, not stop at "there's only one catch clause."

**How it was caught.** Not per-slice review (slice 4, which built the funnel,
and slice 6, which built the write path, each passed individually). The
slice-7 conformance-kit derivation exercises both a read fact and a write
fact for the same missing-tenant condition side by side
(`MissingTenant_ReadAndWrite_SurfaceTheSameDeniedStatus`), and that
side-by-side assertion is what surfaced the divergence — mid-epic, fixed in
6fbcf06, re-verified at epic-close. This reinforces the S3 epic-close lesson
("an epic-close/cross-cutting gate that diffs sibling op classes is
structurally necessary — per-slice review can't see it") with a concrete
mechanism: a conformance kit that asserts parity facts across op classes,
not just per-op-class correctness, is what makes the gap visible without a
human having to think to look for it.

**Residual, tracked non-blocker.** The epic-close security review (attempt 1,
pass) found one more instance of the same root-cause pattern still present:
a general authorization-policy write-deny (`TableMutationPipeline.cs:104`)
also throws a codeless `BifrostExecutionError`, also falls to `INTERNAL`,
while its read-path equivalent maps to `PERMISSION_DENIED`. Judged
non-blocking (fires pre-row-lookup, invariant to row existence, reveals only
the caller's own authorization — not an existence/tenant oracle), but it is
the same defect shape as 6fbcf06 and the fix is the same: tag it with
`AccessDeniedCode` and add a conformance fact. Left as a recorded follow-up,
not fixed in this epic.

**Applicability.** Any adapter (or future adapter) that centralizes error
mapping into one funnel per the OData-doc pattern must, at epic-close, also
verify condition-tag parity at the throw sites feeding that funnel across
every op class it serves — especially where read and write paths call
different Core transformers for what is conceptually the same policy check.
A conformance/parity fact per shared condition (not just per op class) is
the cheapest way to make this checkable by a test rather than by memory.

## Secondary note (not promoted to a separate doc): dead-agent stall recovery

gRPC slice 2 was recovered from a stalled `doing` task (the executing agent
died after commit+build but before test/review). The coordinator re-claimed
the expired lease and treated a rigorous takeover review as the sole
completeness gate — no re-implementation, no discarding of the partial work.
This is a loop-operations pattern, not a bifrostql code lesson; if it
recurs, it belongs in a worktrack-loop steering doc or `.claude/rules`, not
here. Flagged for the coordinator's judgment rather than promoted.
