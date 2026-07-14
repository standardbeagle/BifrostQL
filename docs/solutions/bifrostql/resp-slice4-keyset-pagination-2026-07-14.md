---
written_at: 2026-07-14T13:00:00Z
source_event: task:01KXEBBZNK74PY2ZV2JYDAASS2
module: bifrostql
category: logic-errors
confidence: high
sources:
  - task:01KXEBBZNK74PY2ZV2JYDAASS2
  - git:609468f
  - git:6f72b98
  - git:ab2c71a
tags: [resp, scan, keyset-pagination, cursor, composite-key, comparator, security-containment, protocol-adapter, correctness]
status: steering
recurrence: 1
---

# resp slice 4: SCAN keyset cursor pagination — durable lesson

## Lesson 1 — composite keyset pagination must use the lexicographic
row-value comparator, never per-column AND

The SCAN engine paginates `<table>:*` PK enumeration by keyset (not
offset): a page's last row's key values become the next cursor, and the
next page's WHERE clause resumes strictly after that position. For a
**composite** key `(c0, c1, c2, ...)` ordered by all columns in schema
order, the only correct resume predicate is the lexicographic row-value
comparison:

```
(c0 > v0)
OR (c0 = v0 AND c1 > v1)
OR (c0 = v0 AND c1 = v1 AND c2 > v2)
...
```

The naive-looking `c0 > v0 AND c1 > v1 AND ...` is wrong and silently
corrupts pagination: it both **skips** rows (any row where `c0 > v0` but
`c1 <= v1` is excluded even though it sorts after the cursor) and can
**duplicate** rows across a page boundary depending on tie patterns — so
`union of all pages != all rows exactly once`, with no exception or error
to surface the bug. Review verified the shipped implementation uses the
lexicographic form and ORDER BY includes every key column in schema order
(required for the comparator to correspond to sort order — a comparator
correct for one column order is wrong for another).

**Why it recurs / generalizes**: any keyset/seek-pagination path over a
**multi-column** key — SCAN-style enumeration, cursor-based list APIs,
resumable batch export, change-feed catch-up — reduces to this same
"resume strictly after row X" predicate. Single-column keys have no
comparator ambiguity (`c0 > v0` is already correct), which is exactly why
the composite case is where reviewers and implementers reach for the
naive AND form: it "generalizes" by pattern-matching the single-column
case without noticing the boundary changed from a scalar to a tuple
order.

**Apply when**: adding or reviewing any resumable/cursor enumeration over
a table with a composite primary or sort key, in any protocol adapter or
query surface.

**Prevention**: build the resume predicate as an explicit OR-chain of
"prefix equal, next column greater" clauses (or an equivalent tuple
comparison if the dialect supports row-value constructors), always paired
with `ORDER BY` on every key column in the same order. Add a test that
pages a table with duplicate values in a leading key column and asserts
the union of all pages equals the full row set exactly once (no skip, no
duplicate) — a single-page test cannot catch this class of bug.

## Lesson 2 — an opaque client-supplied cursor must decode to
position-only, never scope; bind it, never concatenate it

The SCAN cursor is `Base64(JSON pk-column-values)` and decodes into
exactly one thing: a `pk > position` predicate. It carries **no**
tenant/policy/soft-delete information. Containment of a forged or
hand-crafted cursor holds not because the cursor is validated against the
caller's scope, but because the surrounding pipeline ANDs the
tenant/policy/soft-delete filter onto every generated query regardless of
what the cursor decodes to — the same transformer chain the read path
always runs (unskippable per the `IQueryIntentExecutor` seam). A cursor
crafted to point into another tenant's key range therefore still resolves
to, at most, the caller's own visible rows resuming from that position —
tested directly (forged cursor into another tenant's range yields only
the caller's own PKs, not a leak and not an error that would reveal the
other tenant's data existed).

The decoded cursor values are coerced to the PK column's CLR type and
passed as bound query parameters — never string-concatenated into
generated SQL — so a cursor value crafted as an injection payload either
fails to coerce (clean decode error) or is bound as an inert literal.

**Why it generalizes**: this is the general safe shape for *any* opaque,
resumable cursor exposed to a client on a protocol adapter or API —
GraphQL `after:` cursors, REST `next_page_token`, any resumable export.
The cursor must encode **position only** (never a filter, scope, or
policy decision), decoded values must be type-coerced and bound as
parameters, and containment must come from the filter pipeline running
unconditionally on every request — not from validating the cursor's
contents against the caller's scope. This lesson does not introduce a new
containment mechanism beyond what AGENTS.md's "transformers unskippable,
adapter has no API to route around them" and
`.claude/rules/protocol-adapter-security.md` invariant 4 (introspection
must reuse the same authorization gate as the data path) already assert
architecturally — it is the SCAN-specific instance confirming that
invariant holds for a cursor-shaped input too, so it is recorded here as
module-level confirmation rather than a new project-level invariant.

**Apply when**: designing any resumable/paginated enumeration exposed to
a client (protocol adapter, GraphQL connection, REST list endpoint).

**Prevention**: keep the cursor payload to position fields only; coerce
each field to its column's CLR type before use and bind it as a
parameter; never let cursor content participate in choosing which
filter/scope applies — that must be the authenticated identity's job, run
unconditionally by the existing transformer chain. Test with a
cursor forged to reference another tenant's/scope's key range and assert
the result set is bounded to the caller's own visible rows (not an error
that would itself leak existence).

## Note — reuse + scope reinforcement (not new, already covered)

SCAN reused the existing `GqlObjectQuery` Sort+Filter+Limit surface
rather than a new query path (keyset over offset — stable under
concurrent inserts, matching Redis SCAN's own eventual-full-iteration
contract, not a strict snapshot). Server-only. `COUNT` is capped at
`Min(count, 1000)` to bound page memory regardless of client-requested
count. Clean run, no rewind; review PASS with 2 low advisories, neither
fixed (an integration-seam test judged slice-6 territory, and a dead
catch clause) — both non-actionable at this slice, not durable lessons.
