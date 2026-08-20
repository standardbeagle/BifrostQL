---
title: "Building Database Dashboards"
description: "Building database dashboards from chart, count-card and table tiles: each tile fetches independently and fails soft, and edit mode is split from view mode."
---

Building database dashboards takes three tile kinds and a grid. A **dashboard**
is a tile grid of saved-object references — chart, count-card, and table tiles —
persisted as a [saved object](/BifrostQL/concepts/saved-objects/) of
`type: dashboard`.

## Tiles

- **Chart** — reuses the [chart panel](/BifrostQL/guides/workbench/charts/) in
  read-only mode.
- **Count card** — a single aggregate value plus a label. The value comes from a
  server [aggregate](/BifrostQL/guides/aggregate-queries/) query, never a
  client-side count.
- **Table** — the first N rows of a saved query, linking to the full grid.

Each tile fetches **independently** through the `useFetcher()` seam (no direct
`fetch`), with its own loading and error state and an optional per-tile refresh
interval.

## Edit vs view mode

Edit mode allows drag, resize, add, and remove; **view mode exposes none of
these affordances** — no editable name, no Save/Rename/Delete, no drag handles,
and no "New dashboard" control. Drag and resize persist to the definition, so
reopening restores each tile's `{ x, y, w, h }` exactly.

## Fail-soft and no cascade

- A tile whose query throws does not unmount or blank the dashboard — sibling
  tiles survive.
- A tile whose referenced saved object was **deleted** renders a tile-level
  error naming the missing id, and every other tile still loads. Deleting a
  saved object performs **no cascade delete** of dashboards referencing it.

## Related

- [Saved objects](/BifrostQL/concepts/saved-objects/)
- [Chart panel](/BifrostQL/guides/workbench/charts/) — the tile that plots a
  server aggregate.
- [Pivot UI](/BifrostQL/guides/workbench/pivot-ui/) — cross-tab designer over
  the same sources.
- [Saved queries](/BifrostQL/guides/workbench/saved-queries/) — what a table
  tile reads.
- [Data workbench overview](/BifrostQL/guides/workbench/)
