---
title: "Pivot queries (potential — not on the roadmap)"
description: "Cross-tab pivot SQL generation exists as a standalone helper. The GraphQL surface is intentionally not wired."
---

:::caution[Status]
**Potential feature. Not on the roadmap.** The helper is shipped and unit-tested across all four dialects, but there is no GraphQL field that triggers it and no resolver that consumes it. Calls only go through the helper API directly.
:::

BifrostQL ships `BifrostQL.Core.QueryModel.PivotSqlGenerator`, a static helper that turns a [`PivotQueryConfig`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/QueryModel/PivotQueryConfig.cs) into parameterized cross-tab SQL. It supports SQL Server's native `PIVOT` operator and a portable `CASE WHEN` fallback for every other dialect.

## What works today

| Capability | State |
|---|---|
| `PivotQueryConfig.Create` validation | shipped |
| SQL Server native `PIVOT (... FOR ... IN (...))` | shipped, unit-tested |
| Engine-agnostic `CASE WHEN` cross-tab | shipped, unit-tested |
| `ISqlDialect.SupportsNativePivot` dispatch | shipped, unit-tested |
| `PivotSqlGenerator.GeneratePivot(dialect, ...)` entry point | shipped |
| `GraphQL` field that returns pivot results | **not implemented** |
| End-to-end tests against real DB engines | **not implemented** |

## What's intentionally missing

No `_pivot(...)` field is emitted by the schema generator. No resolver consumes the helper's `ParameterizedSql`. No `ReaderEnum` branch shapes pivot rows into a GraphQL response. The two-pass flow (`GenerateDistinctValuesSql` to enumerate pivot columns, then `GeneratePivot` to project) has no orchestrator.

That gap is deliberate. Pivot is a wide, low-cardinality shape that doesn't fit the per-row resolver pattern BifrostQL was built around, and the design questions — how does pivot interact with paging, filters, joins, security policies, the aggregate-value type — haven't been answered.

## If you need pivot today

Three viable paths in priority order:

1. **Use `_agg(value: { joinTable: { column: ... } } operation: ...)`** — for the common "group X by Y and aggregate Z" case, the aggregate path already returns one row per group through a normal GraphQL resolver. See [`docs/research/agg-dialect-survey.md`](https://github.com/standardbeagle/BifrostQL/blob/main/docs/research/agg-dialect-survey.md).
2. **Call `PivotSqlGenerator` from a custom resolver in your host** — the helper is public; a module or middleware can build a `ParameterizedSql` and execute it against the `IDbConnFactory`. The output shape and contract are entirely yours.
3. **Pivot in the client** — for low-cardinality pivots, pulling the long-form aggregate and pivoting in TypeScript/SQL keeps the GraphQL contract narrow.

## If you want to drive pivot onto the roadmap

The unblock work is captured in [`docs/research/pivot-dialect-survey.md`](https://github.com/standardbeagle/BifrostQL/blob/main/docs/research/pivot-dialect-survey.md). Open an issue with a concrete use case before opening a PR — the surface design needs to be settled first.
