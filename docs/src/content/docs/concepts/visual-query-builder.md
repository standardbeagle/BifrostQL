---
title: "Visual Query Builder"
description: "The Access-style visual query builder lets desktop users assemble multi-table SELECTs — tables, joins, columns, sorting, and criteria — that BifrostQL turns into parameterized SQL via the dialect layer and runs over the in-process Photino bridge."
---

The **visual query builder** is a Microsoft Access–style query designer in the BifrostQL desktop app. Users pick tables, wire joins, choose columns, and set sorting and criteria in a grid; BifrostQL generates a correct, parameterized `SELECT` for the active database and runs it — all without writing SQL, and without the query ever touching an HTTP or GraphQL endpoint.

## How it fits together

The builder is split across three layers, each independently tested:

1. **`VisualQuerySpec` contract** — a serializable description of the query (tables, columns, joins, an AND/OR filter tree, sort, row limit). The C# records (`BifrostQL.Core.QueryModel.VisualQuery`) and the TypeScript mirror (`frontend/src/lib/visual-query.ts`) are kept field-for-field identical so the spec round-trips over the bridge with no converters. Enum-like fields (sort, join type, filter op, operator) are plain strings to keep both ends in lockstep.

2. **Server-side SQL generation** — `VisualQueryBuilder.Build(spec, model, dialect)` resolves every table and column against the loaded `IDbModel` (an allow-list — anything not in the model is rejected, an injection guard), escapes identifiers and paginates through the active `ISqlDialect`, emits `INNER`/`LEFT` joins with multi-column `ON` for composite foreign keys, builds a parameterized `WHERE` from the filter tree, and adds `ORDER BY` + a clamped row limit. **No identifier or value is ever string-concatenated** — values flow through named parameters (`@p0`, `@p1`, …).

3. **The Photino bridge + React designer** — the designer never calls HTTP. Three in-process bridge handlers serve it:
   - `get-builder-schema` — tables, columns, and FK relationships from the cached `DbModel` (composite FKs included).
   - `build-sql` — spec → `{ sql, parameters }` for the read-only SQL-preview tab.
   - `build-and-exec` — spec → build → execute → the same columnar result shape (`columns`, `rows`, `rowsAffected`, `truncated`) the raw SQL console renders.

## FK auto-join

When a related table is dropped onto the canvas, `FkAutoJoin` derives the join from the model's foreign-key metadata (both directions, composite-aware). A single FK path is wired automatically; multiple paths are surfaced as candidates for the user to choose — the builder never guesses silently. Tables with no detectable relationship fall back to a manual join in the join editor.

## The criteria grid

The Access-style bottom grid maps directly to the filter tree: criteria in the same row are `AND`-ed across columns, and each additional **Or** row becomes an `OR` group. Sort direction and order drive `ORDER BY`. Operators reuse the standard BifrostQL filter set (`_eq`, `_neq`, `_lt`, `_lte`, `_gt`, `_gte`, `_contains`, `_in`, `_between`, `_null`); `_in`/`_between` take comma-separated values.

## Scope

The current builder targets **multi-table SELECTs with joins**. Aggregates (`GROUP BY`/totals), computed expression columns, action queries (`INSERT`/`UPDATE`/`DELETE`), and saved queries are out of scope for now. For arbitrary SQL — including DML/DDL — use the raw **SQL console** pane, which shares the same in-process execution path.

## Building for the desktop app

The React frontend compiles into `src/BifrostQL.UI/wwwroot`, which is **git-ignored**. After changing any designer code, run `pnpm build` in `src/BifrostQL.UI/frontend` so the desktop binary serves the updated UI.
