---
written_at: 2026-07-14T04:33:32Z
source_event: task:01KXEBBBTYFY122VB5AD2XE0PY
module: bifrostql
category: testing-methodology
confidence: high
sources:
  - task:01KXEBBBTYFY122VB5AD2XE0PY
  - git:c398911
  - git:8c49178
  - git:b2bc9c3
  - git:37f66c6
tags: [pgwire, protocol-adapter, conformance-kit, tenant-filter, smoke-test, honest-smoke, fail-closed]
status: steering
recurrence: 1
---

# pgwire slice 6: conformance derivation + honest smoke — durable lessons

Clean run, no rewinds (all 4 workflow steps passed attempt 1, review PASS
0 blockers, 515/515). Lessons below are testing-methodology, not
security-invariant additions — `protocol-adapter-security.md` already
covers the invariants these tests prove; this doc records how to prove
them without weakening the shared kit or faking success.

## Lesson 1 — deriving a shared conformance kit for a sanitizing adapter: adapt the EXPECTED signal via a virtual, never relax the ASSERTED fail-closed check

`ProtocolAdapterConformanceTests` (Echo-derived) asserts fail-closed by
matching a rejection reason FRAGMENT. pgwire's invariant-3 sanitization
collapses every non-translation fault to one generic
`InternalQueryErrorMessage`, so the canonical fragment can't match
verbatim when the kit is derived for pgwire.

Fix pattern: add a virtual `ExpectedRejectionFragment(canonical)` to the
base kit. Default returns `canonical` (Echo's assertions are byte-for-byte
unchanged). pgwire overrides it to return the sanitized string. The
fail-closed PROOF itself is untouched — wire `ErrorResponse` →
`ExecuteReadAsync` throws → zero rows — so a fail-open adapter (rows
returned, no throw) still fails the kit regardless of which fragment is
expected. Reviewer explicitly re-verified this does not create a
fail-open loophole.

Generalizes: when deriving this kit for the next sanitizing adapter
(resp is next), adapt what you EXPECT to see, never touch what you
ASSERT must be true (throw + zero rows).

## Lesson 2 — proving a transformer is applied on a transport path: two identities, one mixed-tenant table, assert disjoint sets + captured SQL

Hollow version of this test: single identity, assert rows come back
non-empty. Real version used here: seed ONE table with rows from two
different tenants, run the IDENTICAL no-WHERE SELECT as two different
identities through a real in-process pgwire loopback (real handshake →
Query → translator/encoders, not a mock, not the `IQueryIntentExecutor`
seam directly), and assert (a) each identity sees only its own subset,
(b) the two result sets don't intersect, (c) the captured generated SQL
carries `WHERE ... tenant_id` with values bound as `@p` params, never
string-authored.

Property that makes it non-hollow: the test would FAIL if the tenant
filter were removed (both identities would see all rows) — it isn't just
checking "some SQL ran."

Reference pattern for proving any transformer is actually reached from a
new transport, not just from the GraphQL/HTTP path it was written for.

## Lesson 3 — HONEST-SMOKE discipline for external tools you can't run in the gate: automate the wire shape, document the real path, label everything

Grafana and Metabase cannot run headless in this gate. Real Grafana/
Metabase connections were NOT made. Instead: automated tests reproduce
the WIRE QUERY SHAPES those drivers issue (introspection queries,
catalog probes) plus a tenant-scoped data SELECT, each test explicitly
labeled VERIFIED-REAL (psql `\dt`, which really is exercised) vs
REPRESENTATIVE (Grafana/Metabase shapes, reproduced not connected) in
the class docstring. A separate runbook
(`docs/src/content/docs/guides/pgwire-bi-smoke.md`) documents the actual
end-to-end manual path and is deliberately NOT wired into `dotnet test`,
carrying an explicit "do not claim passed unless actually run" note.

Reference pattern: when a smoke target can't run in CI, automate the
protocol shapes it would send, keep a documented manual path for the
real thing, and label which parts are verified vs representative —
never fake the external connection to make a test "pass."
