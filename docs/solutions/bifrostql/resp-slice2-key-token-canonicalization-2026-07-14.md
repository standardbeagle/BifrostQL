---
written_at: 2026-07-14T12:00:00Z
source_event: task:01KXEBBTW3AFQQ0J2DGVA79612
module: bifrostql
category: logic-errors
confidence: high
sources:
  - task:01KXEBBTW3AFQQ0J2DGVA79612
  - git:0c5ec99c0ab2098272829406da7c9d5b918be0c8
tags: [resp, batch-mapping, key-token, decimal, numeric-canonicalization, correctness, mget, join-back, advisory-fixed-preclose]
status: steering
recurrence: 1
---

# resp slice 2: GET/MGET/EXISTS/TYPE — durable lesson

## Lesson — batch result-to-input remap by stringified key must use a TYPE-CANONICAL token, not raw ToString

`RespReadEngine` batches MGET's requested PK values into one `_in` query
intent and maps the returned rows back to requested keys via a `KeyToken`
string. Review advisory (fixed pre-close, 0c5ec99): for a decimal/numeric PK
the request-side token (`"1.0"`) and the token derived from the
DB-materialized row value (`"1"` or `"1.00"`) diverged as raw strings, so a
row the `_in` intent *did* return failed to map back to its requested key —
GET/MGET/EXISTS/TYPE reported a miss (Null) for a row that actually exists
and is visible to the caller. It only ever under-returns, never returns the
wrong row for a mismatched key, so this is **not** a security leak — but it
is a real correctness gap for any table with a decimal/numeric primary key.

Fix: derive the token from a canonical form keyed off the PK column's CLR
family, applied **identically** on both the request side and the
result-row side — integers via a common `long` form, decimals via a
trailing-zero-stripped invariant string (`1`, `1.0`, `1.00` all collapse to
one token; `1` and `1.5` stay distinct), everything else via its plain
invariant string. Because both sides run the same classification+format
function, equal values always collide to the same token and distinct values
never accidentally collide.

**Why it recurs / generalizes**: any time a batched or joined result set is
mapped back to its inputs by a *stringified* key — MGET-style PK batches,
`_in`-batch lookups, join-back reconciliation, dedup by key — a raw
`ToString()` on each side is not guaranteed to agree. Numeric scale
(`1` vs `1.0` vs `1.00`), trailing zeros, casing, and culture-specific
formatting (decimal separator, thousands separator) are all divergence
sources between a request-side literal and a DB-round-tripped value of the
"same" logical key.

**Apply when**: implementing any batch-lookup or join-back path that keys
a result-to-input map by a stringified value derived from a typed column —
not limited to RESP; applies to any protocol adapter or resolver doing
batched PK/FK lookups.

**Prevention**: derive the map key from one canonical-token function keyed
by CLR type family, call it identically on the request-literal side and the
result-row side, and add a test asserting that logically-equal-but-lexically-
different literals of the type (e.g. `1`, `1.0`, `1.00` for decimal) collide
to the same token while distinct values do not. Do not derive the token from
independent `ToString()` calls on each side even if they "look" invariant.

## Note — playbook + composite-PK + no-existence-leak reinforcement (not new, already covered by AGENTS.md / protocol-adapter-security.md)

Slice 2 attached GET/MGET/EXISTS/TYPE to slice-1's `IRespCommandHandler`
dispatch table with zero changes to codec/connection/auth. Reused
`TableFilter.FromPrimaryKey` + `IDbTable.KeyColumns` (the composite-PK
helper, not `primaryKeys[0]`) and `IQueryIntentExecutor` so transformers
stayed unskippable. No-existence-leak held: a hidden row and a genuinely
missing key both return Null, indistinguishable to the caller. Clean run,
no rewind, review PASS with 2 advisories (this one + one other) fixed
before close.
