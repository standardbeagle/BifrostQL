---
written_at: 2026-07-17T00:00:00Z
source_event: task:01KXM3WMWEDJABT9X5QYPRVWCJ
module: bifrostql
category: best-practices
confidence: guess
sources:
  - task:01KXM3WMWEDJABT9X5QYPRVWCJ#correctness-review
tags: [odata, protocol-adapter, introspection, authorization, fail-closed, edmx, follow-up-candidate]
status: steering
recurrence: 1
---

# OData slice 2: fail-closed column filtering can produce structurally partial metadata

## Lesson

`ODataModelVisibility` correctly reuses the same authoritative gate the
query path uses (`PolicyEvaluator`/`PolicyConfigCollector.FromTable`,
`PolicyIdentity.FromUserContext`) to filter columns out of the EDMX,
per protocol-adapter-security invariant 4. That invariant was written
for the common case: filtering removes ordinary (non-key) columns, so
the entity's `<Key>` stays intact and the emitted CSDL stays
structurally valid, just narrower.

Slice 2's review surfaced an edge that invariant doesn't cover: if a
column that is **part of the primary key** is itself read-denied while
the table remains visible, `KeyColumns` is filtered by
`visibleColumnNames` the same way ordinary columns are — so the emitted
`<Key>` can end up missing a key column, or (single-column-PK case)
empty. That's not an authorization leak (nothing unauthorized is
exposed), but it is a **spec-compliance** problem: OData v4 requires
every entity type to declare a complete key, and a downstream client
(Excel, Power BI, any conformant OData consumer) parsing a partial or
empty `<Key>` gets malformed metadata for an entity it's otherwise
allowed to see.

## Why it recurs

"Filter columns fail-closed by reusing the query-path gate" is now a
three-times-applied pattern in this codebase (pgwire catalog emulation,
MCP schema surface, OData `$metadata`). Each application filters
columns uniformly — the pattern doesn't distinguish "this column is
merely a projected field" from "this column is structurally required
by the output format's own schema (a key)." Any output format with its
own must-have-a-key-or-be-well-formed constraint will hit this same
edge if column visibility filtering is applied uniformly across all
columns.

## Apply when

Building or reviewing an identity-filtered introspection/metadata
surface (OData `$metadata`, any future catalog/schema endpoint) where
the output format has structural requirements (e.g., "an entity must
declare a key") that ordinary column filtering can violate.

## Prevention (not yet implemented — flagged, not filed as a task)

When a key column is excluded by the visibility filter, the *entity*,
not just the column, should fail closed — omit the whole entity type
from the document (matching the existing "unparseable policy excludes
the table for everyone" precedent) rather than emit a partial/empty
`<Key>`. No fixture in slice 2 exercises this path; it's out of that
slice's acceptance criteria. This is a single-source finding (one
review note, not yet reproduced or hit twice) — treat as `confidence:
guess` until a second occurrence or an explicit fix confirms it.
Candidate owner: OData slice 7 (conformance kit) or a small standalone
fix task before the OData epic closes; flagged here for operator
decision, not auto-filed.
