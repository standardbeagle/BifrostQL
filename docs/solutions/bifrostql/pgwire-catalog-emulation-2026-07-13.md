---
written_at: 2026-07-13T00:00:00Z
source_event: task:01KXEBB73JDTEZQ4QKT6S9T4CB
module: bifrostql
category: best-practices
confidence: high
sources:
  - task:01KXEBB73JDTEZQ4QKT6S9T4CB
  - git:0439525
  - git:abca8d7
  - git:50521bc
  - git:9dd3c9b
tags: [pgwire, protocol-adapter, introspection, authorization, catalog, fail-closed, workflow-gap]
status: steering
recurrence: 1
---

# pgwire slice 4: catalog emulation — durable lessons

## Lesson 1 — synthetic/derived read surfaces belong in an in-memory responder, not the SQL-executing intent path

`pg_catalog`/`information_schema` rows have no physical table, so routing
them through `SqlExecutionManager` (which generates and runs real SQL)
doesn't fit. Approach B (intercept catalog queries, answer from an
in-memory `DbModel` projection under the authenticated `userContext`) was
chosen over Approach A (register catalog as queryable virtual tables through
the intent path) for this reason, and it left the slice-3 parser/translator
untouched. Because the projection is synthetic in-memory data, not a SQL
string, there is no injection surface — in-memory joins/filters over it
(e.g. psql `\d`'s `pg_class ⋈ pg_namespace`) are safe without loosening the
subset parser.

**Apply when**: any protocol adapter needs to expose introspection/metadata
that has no backing table.

## Lesson 2 — introspection surfaces must reuse the query-path authorization, fail-closed (cross-cutting invariant)

Catalog visibility reuses the authoritative gate — `PolicyEvaluator.CanAct`
+ `IsColumnAllowed` via the newly-extracted `PolicyIdentity.FromUserContext`
— the SAME check `PolicyFilterTransformer` (priority 1) enforces on the
data path. `PolicyIdentity` was a behavior-identical lift out of
`PolicyFilterTransformer` (reviewer-confirmed pure extraction), so catalog
visibility and the query-path transformer now share one identity
projection. Unparseable/unevaluable policy excludes the table/column even
for admin (`FromTable` throws before the evaluator runs) — fail-closed.

**Why it recurs**: any new introspection/metadata endpoint is tempting to
implement as a separate, weaker check ("it's just metadata") — this is an
information-disclosure side channel if the reimplementation drifts from the
data-path policy. Promoted to `.claude/rules/protocol-adapter-security.md`
as invariant 4 (see below) since it generalizes beyond pgwire.

## Lesson 3 — catalog-query detection must be parse-based, not substring-based

A raw substring match on `information_schema.`/`pg_catalog.` misroutes a
legitimate user query that merely carries that text in a string literal.
Route by the FROM/JOIN target relation instead (fixed in 50521bc after
initially shipping the substring version).

**Apply when**: writing any query-routing/dispatch logic keyed on relation
identity inside a text protocol.

## Lesson 4 (workflow gap, recurring — 2nd consecutive occurrence)

Second consecutive pgwire slice to change `BifrostQL.Core` while the
pgwire-slice workflow template only gates `BifrostQL.Server.Test` (slice 3:
`FlattenSingleLinkJoins`; slice 4: `PolicyIdentity`). Core tests were run
manually both times, outside the gated workflow. This is a recurring gap,
not a one-off: either the pgwire-slice template should gate Core tests, or
pgwire slices should stop touching Core. Flagged with escalating emphasis
now for the second time — next occurrence should trigger the template fix
rather than another manual workaround.
