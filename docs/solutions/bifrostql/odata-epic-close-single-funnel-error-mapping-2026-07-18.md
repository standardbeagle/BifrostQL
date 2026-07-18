---
written_at: 2026-07-18T00:00:00Z
source_event: epic:01KWXCZ1QDK6A7AJB9VWECKZDX
module: bifrostql
category: security,error-handling,architecture
confidence: high
sources:
  - epic:01KWXCZ1QDK6A7AJB9VWECKZDX
  - doc:docs/solutions/bifrostql/s3-epic-close-crosscutting-error-mapping-2026-07-17.md
  - src/BifrostQL.Server/OData/ODataMiddleware.cs
tags: [protocol-adapter, odata, error-handling, non-enumeration, epic-close-gate, architecture-pattern]
status: steering
recurrence: 1
---

# OData epic close: single-funnel error mapping makes the S3 oracle bug class unreachable by construction

**Lesson.** The OData v4 read-endpoint epic (8 slices: service-doc, `$metadata`,
entity-read, `$filter`, `$skiptoken`, `$expand`, HTTP front door, host wiring)
passed its epic-close security review on attempt 1 with zero blockers. The
reviewer specifically checked for the S3 epic's differential-error-mapping
oracle defect (`s3-epic-close-crosscutting-error-mapping-2026-07-17.md`: read
op classes caught fewer exception types than write op classes, producing
GetObject=500 vs ListObjects=404 for the identical denied/corrupt condition)
and found it **structurally impossible** to reproduce here: `ODataMiddleware
.InvokeAsync` wraps every op class — service-doc, `$metadata`, entity-read,
`$filter`, `$skiptoken`, `$expand` — in ONE top-level try/catch
(`catch (ODataProtocolException)` → curated message,
`catch (OperationCanceledException) when (...)` → client-disconnect no-op,
`catch (Exception)` → generic sanitized InternalError, detail logged
server-side only). There is no per-op-class catch clause to drift, so there
is no seam for review to police — the invariant holds by construction, not
by discipline.

## The forward-looking pattern

The S3 epic-close lesson's generalization was **detection**: "any multi-slice
epic implementing one cross-cutting contract across N slices needs an
epic-close gate that reviews the seam, not the slice." That is necessary but
reactive — it only catches divergence that has already happened, and only if
someone remembers to run the cross-op-class diff.

This OData epic demonstrates the **prevention** counterpart, and it is the
piece worth carrying forward: **when a protocol adapter has multiple op
classes that share one exception→wire-status contract, route ALL of them
through a single top-level error-mapping funnel (one try/catch at the
request-dispatch boundary) instead of N per-op-class catches kept in sync by
review.** A single funnel cannot diverge between op classes because there is
only one catch clause to diverge from. This doesn't replace invariants 1, 5,
and 9 in `.claude/rules/protocol-adapter-security.md` (the funnel's own catch
set must still be complete and must still catch the right exception family)
— it collapses N places that could get invariant 9 wrong into one.

**Applicability.** This pattern fits adapters where request dispatch already
has one natural boundary (HTTP middleware `InvokeAsync`, a single connection
read-loop) — OData, and by extension any future HTTP-hosted adapter. It does
not directly fit adapters where op classes are handled by genuinely separate
code paths at different layers (S3's GetObject vs PutObject are separate
resolver methods with no shared dispatch frame) — for those, invariant 9's
detection discipline (epic-close seam review, catch-set symmetry checklist)
remains the applicable control. When designing a new adapter's error
mapping, prefer restructuring toward one dispatch funnel wherever the
transport allows it; fall back to the seam-review discipline only where the
transport genuinely forces per-op-class catch sites.

## Secondary observation: the adapter spine is now proven across five adapters

OData is the fifth protocol adapter (after pgwire, RESP, S3, MCP) to reuse
the same three seams unmodified: reads via `IQueryIntentExecutor`, identity
via `IBifrostAuthContextFactory` (shared, fail-closed), and introspection
filtered through the same `PolicyEvaluator`/`IsColumnAllowed` gate the query
path enforces (invariant 4). No adapter has needed to fork or special-case
any of the three. This is a maturity signal, not a new lesson: the adapter
spine in AGENTS.md's "Request Flow" section is validated as genuinely
adapter-agnostic infrastructure, and future adapters (gRPC, LDAP, Prometheus
are in-queue) should default to reusing it rather than re-deriving.

## Why this is new relative to existing docs

`s3-epic-close-crosscutting-error-mapping-2026-07-17.md` and
`pgwire-epic-playbook-2026-07-14.md` both state the detection-side lesson
(review the seam across op classes at epic-close). Neither states the
structural/prevention pattern (collapse to one funnel so there's no seam to
diverge). This doc fills that gap.
