---
written_at: 2026-07-18T00:00:00Z
source_event: task:01KXM3X0E0XCKC4D3JEEN2SD0J
module: bifrostql
category: best-practices
confidence: high
sources:
  - task:01KXM3X0E0XCKC4D3JEEN2SD0J
  - git:0539d76
  - git:f58750f
tags: [odata, expand, relationship-join, composite-fk, query-intent-executor, protocol-adapter]
status: steering
recurrence: 1
---

## Lesson

When an acceptance criterion says "reuse existing relationship/join
machinery" for a new adapter read feature, verify what that machinery
actually does before building on it — `SqlExecutionManager`'s join-flatten
seam only merges **to-one, single-column** links onto root rows and
explicitly **drops collection joins and composite-FK joins**. It is not a
general relationship-serving primitive.

## What didn't work (as originally scoped)

OData slice 6's task content and file scope
(`tests/.../QueryIntentJoinFlattenTests.cs`) pointed at extending the
flatten path for `$expand`. The flatten path can't produce a to-many
nested shape (it flattens onto one row) and can't resolve a composite FK
without an adapter-built partial predicate — the exact `column[0]` guess
`.claude/rules/composite-pk-compliance.md` forbids. Building `$expand` on
that seam would have meant reimplementing collection semantics or silently
breaking composite-FK compliance.

## Why it recurs

Any new adapter or feature that wants to "expand"/nest a related entity
(gRPC nested reads, a future GraphQL-adjacent surface, further OData
navigation work) will be tempted to reuse the same flatten seam because
it's the one that already exists and is named for relationships. The seam
was built for a narrower case (root-row decoration with a to-one value)
and doesn't generalize.

## Apply when

Building any relationship/navigation-expansion feature on an adapter that
sits behind `IQueryIntentExecutor` / `IMutationIntentExecutor`.

## Prevention

- Serve each expanded navigation level as an **independent scoped child
  intent** through `IQueryIntentExecutor` (target key `_in` the parent
  batch's schema-derived key values, capped at a fanout limit) rather than
  trying to fold it into the parent's flattened row set. Each child intent
  re-runs the full tenant/soft-delete/policy transformer chain
  independently — stronger scope-inheritance than a single flattened join,
  and works uniformly for to-one and to-many.
- For composite-FK relationships the adapter cannot bind through a
  single-key intent: **reject with a deterministic error** (slice 6 used
  400) rather than guess the first column pair. This is the composite-FK
  case of the general composite-pk-compliance rule applied to a *read*
  seam, not just mutation/UI code.
- When a task's acceptance criteria or file scope names a specific
  existing code path as the implementation mechanism, confirm what that
  path actually does (read it, don't assume from its name) before
  treating divergence as a deviation to justify — it may be the criteria
  describing a mechanism that doesn't fit, per the sibling lesson in
  `docs/solutions/bifrostql/mcp-slice6-criteria-vs-implemented-divergence-2026-07-17.md`.
