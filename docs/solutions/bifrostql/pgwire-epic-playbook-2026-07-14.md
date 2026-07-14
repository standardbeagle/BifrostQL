---
written_at: 2026-07-14T00:00:00Z
source_event: epic:01KWXCZ1J3CKK8XA58MM1SAS7N
module: bifrostql
category: release-methodology
confidence: high
sources:
  - epic:01KWXCZ1J3CKK8XA58MM1SAS7N
  - git:0e24ab8
  - .worktrack/docs/pgwire-slice1-handshake-lessons.md
  - .worktrack/docs/pgwire-slice2-simple-query-lessons.md
  - .worktrack/docs/pgwire-slice3-sql-parser-lessons.md
  - docs/solutions/bifrostql/pgwire-catalog-emulation-2026-07-13.md
  - docs/solutions/bifrostql/pgwire-extended-query-bind-decode-2026-07-13.md
  - docs/solutions/bifrostql/pgwire-conformance-derivation-and-honest-smoke-2026-07-13.md
  - .claude/rules/protocol-adapter-security.md
tags: [protocol-adapter, epic-playbook, pgwire, resp, methodology, seam-pattern, conformance-kit]
status: steering
recurrence: 1
---

# Protocol-adapter epic playbook (derived from pgwire, 7 slices, merged 0e24ab8)

This is the EPIC-LEVEL methodology that made the pgwire (Postgres wire
protocol) front door land in 7 slices, 4 clean passes, 1 rewind, 0 escapes
to main. It is written for the NEXT protocol epic (resp / Redis wire
protocol, 6 slices) to follow. It does not restate the 5 cross-cutting
security invariants already in `.claude/rules/protocol-adapter-security.md`
— read that file first — nor the per-slice tactical detail already captured
in the linked docs below. This doc is the connective tissue: the slice
shape, the seam discipline, and the process signals that recurred across
the whole epic.

## 1. The slice decomposition that worked

1. **Handshake + auth + identity, fail-closed.** Land `IBifrostAuthContextFactory`
   wiring first; stub everything downstream (`feature_not_supported`). See
   `.worktrack/docs/pgwire-slice1-handshake-lessons.md`.
2. **Simple request/response + result encoding + error mapping.** First real
   read path, first error-sanitization decision. See
   `.worktrack/docs/pgwire-slice2-simple-query-lessons.md`.
3. **Request→intent translator, safe subset, no raw passthrough.** Touches
   Core (`SqlExecutionManager.FlattenSingleLinkJoins`). See
   `.worktrack/docs/pgwire-slice3-sql-parser-lessons.md`.
4. **Catalog/introspection emulation, identity-filtered.** Touches Core
   (`PolicyIdentity` extraction). See
   `docs/solutions/bifrostql/pgwire-catalog-emulation-2026-07-13.md`.
5. **Advanced protocol (extended query / pipelining) + control messages
   (cancel, connection limits).** Highest-risk slice — untrusted-input
   decode paths multiply. See
   `docs/solutions/bifrostql/pgwire-extended-query-bind-decode-2026-07-13.md`.
6. **Conformance-kit derivation + tenant-filter integration proof + honest
   smoke.** See
   `docs/solutions/bifrostql/pgwire-conformance-derivation-and-honest-smoke-2026-07-13.md`.
7. **Docs.**

Slices 3 and 4 are the ones that touch `BifrostQL.Core`, not just the
adapter project. This recurred as a **workflow-gate gap**: the pgwire-slice
workflow template's build/test steps only targeted
`tests/BifrostQL.Server.Test`, so Core's test suite (4791 tests) had to be
run manually by the implementer on both Core-touching slices, flagged
twice in the per-slice docs and never fixed at the template level during
this epic. **For resp**: before slice 3 (translator) or any slice that is
predicted to touch Core, patch the workflow template to gate
`BifrostQL.Core.Test` too, rather than repeating the manual-run workaround
a third time.

## 2. Land-the-seam / defer-implementation — the pattern that carried the whole epic

Every slice that had an obvious later-swappable core landed a narrow
interface first and stubbed the concrete backing:

- `IPgCredentialStore` (slice 1) — handshake-only, no OIDC token-exchange yet.
- `IPgQueryTranslator` (slice 2 → 3) — slice 2 scoped it to
  `SELECT <cols|*> FROM <table>`; slice 3 replaced it with a full SQL-subset
  parser via **one DI registration line**, leaving `PgConnectionHandler`,
  `PgBackend`, `PgTypeMap`, `PgValueEncoder` untouched.
- `IPgCatalogResponder` (slice 4) — catalog answered from an in-memory
  `DbModel` projection, not the SQL-executing intent path.

The confirmed property: the swap crossed a full round-trip (protocol loop,
encoders, tests) with zero collateral edits outside the DI registration.
**For resp**: identify the analogous seams up front (e.g. a
`IRespCredentialStore`/auth stub in slice 1, a `IRespCommandTranslator`
scoped to a minimal command subset before the full command surface lands)
and hold the discipline of "swap one line" as the acceptance bar for
whether a slice boundary is cut correctly.

## 3. Security spine every protocol adapter must reproduce

Full detail lives in `.claude/rules/protocol-adapter-security.md` (5
invariants, do not restate here) — checklist form for slice planning:

- Identity always through `IBifrostAuthContextFactory`, fail-closed.
- Reads always through `IQueryIntentExecutor` — transformers unskippable.
- Introspection/metadata filtered by the SAME `PolicyEvaluator` gate as the
  data path, fail-closed (never a separate "it's just metadata" check).
- Client-facing error text: only the adapter's OWN curated exception type
  forwards verbatim; every `BifrostExecutionError` (and everything else)
  sanitizes to a generic message, full detail logged server-side.
- Any decode of a typed value out of untrusted wire bytes catches the FULL
  parse-exception family (`FormatException or OverflowException or
  ArgumentException`), never just the "obviously malformed" one.

These recurred as review findings 3 times across pgwire in different
guises (verbatim error forward, overflow→syntax-error, overflow→bind-crash)
— treat wire-decode review as a standing checklist item for every resp
slice, not a one-time check.

## 4. Conformance-kit derivation pattern (resp derives the SAME kit)

Subclass `ProtocolAdapterConformanceTests`, set
`AdapterSupportsMutations`, and if the adapter sanitizes errors (it will),
override the `ExpectedRejectionFragment(canonical)` virtual to return the
sanitized string — **never** relax the underlying ASSERTED fail-closed
check (wire error → executor throws → zero rows). The adaptation is only
in what you EXPECT to see on the wire, never in what you ASSERT must be
true. Full mechanics in
`docs/solutions/bifrostql/pgwire-conformance-derivation-and-honest-smoke-2026-07-13.md`.

Pair it with the tenant-filter integration proof pattern from the same
doc: two identities, one mixed-tenant table, assert disjoint result sets +
captured parameterized SQL — a test that would fail if the transformer
were removed, not one that merely checks "some SQL ran."

## 5. Honest-smoke discipline for tools you can't run headless

Automate the WIRE SHAPES the external tool would send (introspection
queries, catalog probes), label each test VERIFIED-REAL vs
REPRESENTATIVE in the class docstring, and keep a separate manual runbook
explicitly marked "do not claim passed unless actually run." Never fake
the external connection to make a test "pass." Full pattern in the
conformance-derivation doc above — apply verbatim to whatever
Redis-client/tool resp needs to honest-smoke.

## 6. Process signals from this epic

- 4 of 5 code slices (1, 2, 3, 4) passed review on attempt 1, with
  non-blocking advisories folded into a same-day fix commit before close —
  now a stable 3x+ convention: advisories touching a declared invariant
  (error sanitization, subset completeness, timing safety) get fixed
  same-day, not deferred.
- Slice 5 took the epic's ONE rewind: a fail-open `OverflowException` in
  Bind parameter decode, caught by correctness-review attempt 1 (blocker),
  fixed, re-reviewed to a clean pass. Recorded as confirmation the
  rewind path works, not a process failure — the highest-risk slice
  (advanced protocol / untrusted decode) is exactly where the gate should
  and did catch something real.
- Stacked-branch strategy: each slice branched off the prior slice's tip;
  the epic merged the final tip to main as ONE fast-forward (28 commits,
  0e24ab8). Preserves per-slice bisectability while keeping the epic-level
  merge trivial.

## For the resp epic

Map this slice shape onto RESP (handshake/auth may be trivial or absent
depending on RESP2/3 + AUTH command semantics — do not skip the fail-closed
identity slice even if RESP has no native handshake), reuse the seam
pattern for command translation, derive the same conformance kit, and
budget the Core-test-gate fix into slice planning instead of repeating the
manual-run workaround a third time.
