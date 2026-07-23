---
title: "Docs identifiers (esp. generated-schema SDL type names) must grep back to their emission site, not be paraphrased or guessed"
written_at: "2026-07-23T22:47:01Z"
source_event: "task:01KWXEA37SE93RT8XYVZ2317WV"
module: "bifrostql"
category: "docs-correctness"
confidence: "high"
form: "constraint"
recurrence: 2
sources:
  - "task:01KWXEA37SE93RT8XYVZ2317WV#review-attempt-1-blockers"
  - "git:d109208"
  - "git:cf951eb"
  - "src/BifrostQL.Core/Model/DbTable.cs:43,45"
  - "src/BifrostQL.Core/Schema/TableSchemaGenerator.cs:134,147"
tags: ["docs", "sdl", "schema-generation", "docs-follow-verified-source", "rewind", "template-gap", "workbench"]
status: steering
related:
  - "docs/solutions/bifrostql/mcp-slice6-criteria-vs-implemented-divergence-2026-07-17.md"
---

## Lesson

Every GraphQL type-name / identifier token inside a docs SDL snippet must be
greppable back to the exact string the code interpolates (a `DbTable *TypeName`
property or a `Surface` constant) — never paraphrased, guessed, or copied from a
spec/prompt, even when the surrounding field names and arg shapes were verified.

## What didn't work (rewind)

Task 5.4 (docs & metadata hygiene). Review attempt 1 FAILED with 2 major
blockers of one root: `guides/aggregate-queries.md` and `concepts/pivot.md`
documented `ordersColumnEnum` / `ordersFilter` in hand-written SDL snippets.
The generator emits neither — it interpolates
`DbTable.ColumnEnumTypeName => "{GraphQlName}Enum"` (= `ordersEnum`) and
`DbTable.TableFilterTypeName => "TableFilter{GraphQlName}Input"`
(= `TableFilterordersInput`) at `TableSchemaGenerator.cs:134,147`. A user
writing typed variables from the doc (`query($g: [ordersColumnEnum!])`) gets a
schema-validation error, and the page contradicted the already-correct
`concepts/schema-generation.md`. Attempt 2 PASSED after every name was derived
character-for-character from `DbTable.cs`; reviewer independently re-verified
against the emission site, including adjacent names the blockers never named
(`ordersPivot`, `PivotAggregate`, `orders_aggregate`).

## Why it recurs

- The author DID apply docs-follow-verified-source to **field names and arg
  shapes** (read `GetAggregateFieldDefinition` / `GetPivotFieldDefinition` for
  structure) but transcribed **readable-looking type names** instead of the
  emitted ones. Hand-written SDL snippets are a drift vector *distinct* from
  field names — verifying one does not cover the other.
- BifrostQL's naming is non-obvious and invites plausible-but-wrong guesses:
  the column enum is `<table>Enum` (not `<table>ColumnEnum`) and the filter
  input is `TableFilter<table>Input` — a **PREFIX** form, not the intuitive
  `<table>Filter` suffix.
- This is the **2nd occurrence** of the docs-must-derive-from-verified-source
  family. The 1st (`mcp-slice6-criteria-vs-implemented-divergence-2026-07-17.md`)
  was docs vs a wrong *acceptance criterion*; this is docs vs the author's own
  plausible *invention* of SDL tokens — same root, different tempting authority.
  Promotion threshold is N=3 per project convention: **do NOT auto-promote —
  flag for operator decision** (candidate target: a `.claude/rules` docs-source
  constraint, or the docs SDL-token lint below).

## Apply when

Any docs task presenting generated-schema SDL, GraphQL type names, or any
identifier the server computes rather than the author chooses.

## Prevention

- Grep the generator's interpolation site (`*TypeName` properties in
  `DbTable.cs`, `Surface` constants) and transcribe the literal string; never
  paraphrase. A live introspection / SDL dump is the safest source.
- Cross-check against `concepts/schema-generation.md`, which already carries the
  real names.
- Proposed mechanical guard (both attempt-1 blockers and the reviewer's manual
  re-verify would have been eliminated): a docs lint that greps new/changed SDL
  code fences for type-name tokens and fails any token absent from a
  generated-schema dump.

## Secondary lesson — backend-slice template leaves a docs-tagged task's primary gate untested (2nd occurrence)

The `backend-slice` workflow template runs only `dotnet build/test` steps
(build-core, build-server, test-core, test-server, correctness-review). A
`docs`-tagged task attached to it has its primary acceptance gate —
`pnpm --dir docs build` — executed by NO deterministic step; both the
implementer and the reviewer had to run it **manually** on both attempts. This
is the 2nd occurrence (1st: task 5.3 binary transport, where JS suites +
frontend build were likewise reviewer-run manually) and is undocumented until
now. **Prevention:** either add a `pnpm --dir docs build` command step to
`backend-slice`, or route docs-tagged tasks to the `docs-default` template.
Flag for operator decision — N=3 not yet reached. `form: procedure` for this
sub-lesson.

## Minor (task-metadata, low durability)

Task 5.4 `fileScope` points `MetadataKeys.cs` at `src/BifrostQL.Core/Model/`;
it lives only at `src/BifrostQL.Abstractions/Model/MetadataKeys.cs` (moved in the
Abstractions carve-out). Carried as an advisory across both review attempts —
fix the standing gate's scope, not a code change.
