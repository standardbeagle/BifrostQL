---
written_at: 2026-08-22T16:30:00Z
source_event: session:schema-validation-feature-2026-08-22
module: bifrostql
category: logic-errors
confidence: high
sources:
  - git:5d0a3abc
  - git:dd83ac3a
  - git:59935fc8
  - git:c0b59d9b
  - git:1d530dee
  - git:93f1845d
tags: [validation, schema-facts, dead-code, type-mapper, radix-select, edit-db, demo-harness, fixture-diversity]
status: steering
recurrence: 1
---

# Schema-derived validation: three lessons from one feature drain

## 1. Verify a derivation's INPUT SHAPE at its source before trusting it

The pre-existing "DB-derived maxLength" rule (`ValidationRules.ExtractDbMaxLength`)
and the `_dbSchema` precision/scale fields both parsed parens out of
`ColumnDto.DataType` — but the INFORMATION_SCHEMA readers put a BARE type name
there ("nvarchar", never "nvarchar(50)"), and `ColumnDto.FromReader` silently
DISCARDED the `CHARACTER_MAXIMUM_LENGTH` / `NUMERIC_PRECISION` / `NUMERIC_SCALE`
columns the readers already selected. Result: the entire derivation was dead
code on SQL Server, MySQL, and Postgres — it only ever fired on SQLite declared
types and hand-built test fixtures, so a green suite proved nothing about
production. **The tell:** a string-parsing derivation whose input comes from a
reader you have not read. Check what the reader actually stores before writing
(or trusting) a parser over it; capture the structured fact at the source
instead (fix: `ColumnDto.CharacterMaxLength/NumericPrecision/NumericScale`,
`DeclaredTypeFacts` as the declared-text fallback).

## 2. Engine-specific value ranges belong on the dialect seam, with honesty about what the engine can't tell you

Storable ranges (integer widths, temporal windows) are ENGINE facts, so they
went on `ITypeMapper` (`GetIntegerRange` / `GetTemporalRange`) with
provider-neutral defaults — not into Core validation logic. Two traps that
would have caused FALSE REJECTIONS, the worse failure mode for validation:

- MySQL `information_schema` does not expose signedness (`int` vs `int
  unsigned` both report "int"): assert the signed∪unsigned UNION, never the
  signed range. Nothing valid is refused; everything outside the union fails in
  the engine whichever signedness applies.
- SQLite type affinity stores 64-bit whatever the declared name — a column
  declared `int` happily holds `long.MaxValue` — so the SQLite mapper declares
  NO narrower range. Validating the declared NAME would refuse values the
  engine accepts.

Same shape for temporal: MySQL TIMESTAMP's ceiling is session-timezone
dependent, so the asserted window is conservative (refuse only what is out of
range in EVERY timezone; let the engine arbitrate the sliver).

## 3. Radix Select emits onValueChange('') when the selected item unmounts — treat '' as noise, never as a user action

Found ON CAMERA while recording the demo (poster review, third catch for that
practice): every populated FK column in the edit dialog rendered as a
placeholder and Save failed "required". Root cause chain, proven with a jsdom
repro test after two live-probe screenshots: the FK select first renders a
fallback raw-key item, then swaps it for the fetched option-window item; the
swap unmounts the item matching the current value, Radix emits
`onValueChange('')`, and the handler treated it as a user selection — silently
clearing the stored FK in the form store. Every FK select on every dataset was
affected; text inputs on the same form were fine, which is what made the
per-field debugging (`FKDBG` prints of defaults vs store value) decisive.

Fix: `guardSelectClear` drops '' at every Select (FK shell, enum, tri-state
boolean) — legitimate selections are always a real value or the `NONE_VALUE`
sentinel. Rule of thumb for any Radix Select over DYNAMIC items: the '' emission
is a lifecycle artifact of item unmount, not user intent.

**Fixture corollary** (extends `regression-test-non-vacuous.md`): the BigInt
precision test's fixture value `12345678901234567890` exceeded int64 — a value
NO bigint column can store — and only ever passed because nothing bounded it.
A fixture value must be physically storable in the column type it claims to
exercise, or the test documents impossible behavior and breaks the moment a
real bound arrives.

## Demo-harness notes (adds to the workbench demo memory)

- CRM dataset now carries the `invoices` table with DECLARED SQL types
  (NVARCHAR(12), DECIMAL(7,2), SMALLINT, DATETIME) precisely because SQLite
  enforces none of them — the on-camera refusals are provably BifrostQL's.
  `QUICKSTART_CARDS` in `docs/videos/capture.mjs` now maps `crm`.
- Debug loop that worked: throwaway Playwright probe (`.work/probe-fk.mjs`,
  resolves deps only under `docs/`) to capture the live GraphQL exchange +
  screenshot → jsdom repro test in `data-edit.test.tsx` → component-level
  prints → fix → re-record. Attribution first (probe an UNTOUCHED dataset) to
  separate "my change broke it" from "pre-existing".
