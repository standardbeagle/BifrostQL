---
written_at: 2026-08-22T16:30:00Z
source_event: session:schema-validation-feature-2026-08-22
module: bifrostql
category: logic-errors
confidence: high
sources:
  - git:5d0a3abc
  - git:dd83ac3a
  - git:1d530dee
tags: [validation, schema-facts, dead-code, type-mapper, radix-select, edit-db]
status: steering
recurrence: 1
---

# Schema-derived validation drain: three lessons

1. **Read the reader before parsing its output.** `ExtractDbMaxLength` parsed
   parens off `ColumnDto.DataType`, but the readers store a bare name
   ("nvarchar") and discarded `CHARACTER_MAXIMUM_LENGTH` — dead code on
   SQL Server/MySQL/Postgres, green in tests. Fix: capture the structured
   facts at the source (`ColumnDto.CharacterMaxLength` etc.).

2. **Engine ranges live on `ITypeMapper`; never risk false rejections.**
   MySQL hides signedness → assert the signed∪unsigned union. SQLite affinity
   stores 64-bit regardless of declared name → assert no named-type range.

3. **Radix Select emits `onValueChange('')` when the selected item unmounts**
   (FK option-window swap). Handled as user input, it cleared every FK select;
   `guardSelectClear` drops it — '' can never be a real selection. Caught by
   demo poster review, proven by jsdom repro test.

Fixture corollary (→ regression-test-non-vacuous.md): a fixture value must be
storable in its column type; the BigInt test's value exceeded int64.
