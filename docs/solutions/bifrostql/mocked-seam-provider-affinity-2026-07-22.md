---
written_at: 2026-07-22T22:00:00Z
source_event: task:01KY4Q2DTN6MQAY2CVFAS43YZD
module: bifrostql
category: test-failures
confidence: high
form: constraint
promotion_candidate: true   # 2nd occurrence of the SQLite/provider value-affinity family (resp-slice2 was 1st); N=3 gate not yet reached — do NOT auto-promote to .claude/rules
sources:
  - task:01KY4Q2DTN6MQAY2CVFAS43YZD#comment-01KY5T0ZNE   # blocker finding (worktrack_finding_v1)
  - task:01KY4Q2DTN6MQAY2CVFAS43YZD#comment-01KY5WMH2Y    # review_annotation_v1 systemicObservations
  - git:9bb61b5   # RED: pin SQLite string-timestamp read
  - git:f67e63a   # GREEN: read provider string timestamp as UTC
  - git:28d4998   # seed alignment (since lexical-comparison secondary risk)
related:
  - docs/solutions/bifrostql/resp-slice2-key-token-canonicalization-2026-07-14.md   # SAME provider value-affinity family (decimal 1.0 vs 1)
tags: [mocked-seam, provider-affinity, sqlite, type-affinity, conformance-slice, first-real-pipeline, missing-criteria, blocker-routing, rewind]
status: steering
recurrence: 1
---

# RSS feeds slice 5: mocked-seam green proves shape, not provider type-affinity

## Lesson (constraint)

N slices green over a MOCKED `IQueryIntentExecutor`/`IMutationIntentExecutor`
seam prove the seam's SHAPE (contract, call order, transformer wiring) but
NOT provider type-affinity — SQLite TEXT storage classes, decimal scale, date
formats. The FIRST real-pipeline integration run is a DISTINCT verification
event and must land EARLY in an epic (or the seam slice's own acceptance
criteria must include one real-provider round-trip), never only in the final
conformance slice.

## What didn't work

- Slices 1–4 tested `FeedReadPlanner` against a mocked executor returning rows
  carrying `DateTime` values → 75–79 green unit tests, feature 100% broken on
  real SQLite. Microsoft.Data.Sqlite materializes a `datetime`/`timestamp`
  column (SQLite storage class TEXT) as `System.String`; the intent seam
  deliberately returns raw provider values; `ReadTimestamp` accepted only
  `DateTime`/`DateTimeOffset` and threw `FeedException 'unsupported type
  String'` — every SQLite feed failed to render.
- Model validation PASSED (column known date/time-typed) while the runtime
  intent value was a string — model type and materialized CLR type diverged,
  and only the real pipeline exposed it.
- Secondary, same-family: `BuildSinceFilter` binds a typed `DateTime`, which
  Microsoft.Data.Sqlite renders as `'yyyy-MM-dd HH:mm:ss'` TEXT; SQLite
  compares TEXT datetimes LEXICALLY, so an ISO-`'T'`-seeded fixture compared
  correctly only at whole-date boundaries (a `'T'`(0x54) > `' '`(0x20)
  accident) and silently mis-included sub-day `since`. Fixed by aligning the
  seed to the binder's format (28d4998), not by changing the correct typed
  binding.

## Why it recurs

The intent executors return raw provider values by design, so provider
value-representation (TEXT-affinity datetime, decimal scale/canonicalization,
zone formatting) is invisible to any suite that hands the seam pre-typed mock
rows. This is the SAME family as resp-slice2 (decimal PK key-token `"1.0"` vs
DB-materialized `"1"` mismatch) — 2nd occurrence. It is structurally the
"fixture too simple to let the bug manifest" family
(`.claude/rules/regression-test-non-vacuous.md`; protocol-adapter-security
invariant 8 fixture rule).

## Apply when

- Building any protocol adapter / feature that reads a typed value
  (date/time, decimal, GUID) from `IQueryIntentExecutor` — gRPC, OData,
  Prometheus adapters reading date columns over SQLite likely share this
  exposure and today have only mocked-executor coverage.
- Planning a multi-slice adapter epic: the closing slice must run the real
  seeded host/pipeline, not mocks; treat mocked-only green as UNPROVEN.

## Prevention

- Seam-slice acceptance criteria: include ONE real-provider round-trip
  (seeded SQLite through the real host) that materializes each typed value the
  feature depends on — do not defer all real-pipeline execution to the final
  conformance slice.
- Adapter reading a typed value must normalize the raw provider value itself,
  fail-closed (feed fix: `DateTimeOffset.Parse(InvariantCulture,
  AssumeUniversal|AdjustToUniversal).UtcDateTime`; unparseable → typed
  exception, per protocol-adapter-security invariant 5's catch family).
- Date-range predicate over SQLite: bound parameter format and stored TEXT
  must share a lexical format; pick a sub-day boundary discriminator so the
  test fails under the wrong representation.

## Process-positive (pattern worth copying)

On surfacing a prior-slice defect OUTSIDE this task's file_scope, the
implementer HALTED, filed a structured `worktrack_finding_v1` blocker
(root cause + surgical fix + secondary risk), committed nothing (no
scope-green state), and resumed only after a coordinator scope grant — then
left a revert-provable RED (9bb61b5) before GREEN (f67e63a). The reviewer
re-proved the fix by reverting f67e63a and confirming exactly the 3 new
string-timestamp tests failed. This is the correct response to an
out-of-scope defect: route a finding, never scope-creep unilaterally, always
leave a revert-provable RED.
