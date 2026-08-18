---
title: "Charts from SQL Aggregate Queries"
description: "Charts from SQL aggregate queries: bar, line, pie and area panels bound to a server GROUP BY query, with theme-aware colors and a hard 100-category guard."
---

The **chart panel** (recharts) builds charts from SQL aggregate queries: bar,
line, pie, and area charts bound to the server
[aggregate](/BifrostQL/guides/aggregate-queries/) surface. A chart's
configuration saves as a [saved object](/BifrostQL/concepts/saved-objects/) of
`type: query`.

## How a chart is built

A chart configuration is JSON: `{ source, dimensions, measures: [{ column, op }],
chartType }`. The panel translates it into a **grouped aggregate GraphQL query**
against the table's `<table>Aggregate` root field, through the query-builder
helpers (schema-derived names only — no user string is interpolated into
GraphQL). Dimension values map to the category axis and measures to series.

### One dimension per chart

`dimensions` is an array in the stored JSON, and the query builder reads
`dimensions[0]`. A chart therefore groups by exactly **one** column. Save a
chart with an empty `dimensions` and the builder raises `Choose a chart
dimension.` The extra array slots are reserved; they do not add a second
grouping level today.

Each measure selects `_count` or a `_sum` / `_avg` / `_min` / `_max` group over
one column. The aggregate surface takes `filter` and `groupBy` only — it has no
pagination arguments, so a chart query carries no `limit`.

## Server-side values only

Every value a chart renders comes from a server grouped-aggregate query; the
panel performs **no client-side summation** over fetched page rows. Clicking
**Visualize** on the grid with an active column filter produces a chart whose
aggregate query carries that same filter, so the rendered totals match the
filtered SQL.

## Edge handling

- **High cardinality.** A dimension with thousands of distinct values does not
  freeze the UI. The result-mapping step caps categories at
  `MAX_CHART_CATEGORIES` (100) and raises `Too many categories (maximum 100).
  Refine the chart filter.` before any renderer receives a row. The guard raises
  a hard error; tighten the filter to bring the group count under the cap.
- **NULL dimensions** render with an explicit label, distinct from the empty
  string.
- **Empty result** renders a friendly empty state, not a blank canvas.

## Theming

Chart colors resolve from theme tokens, so charts render correctly in both light
and dark themes rather than from hard-coded hex values.

## Related

- [Dashboards](/BifrostQL/guides/workbench/dashboards/) — reuse a chart as a
  read-only dashboard tile.
- [Pivot UI](/BifrostQL/guides/workbench/pivot-ui/) — cross-tab the same data
  across two axes instead of one.
- [Grid grouping](/BifrostQL/guides/workbench/grouping/) — the same server
  `GROUP BY`, rendered as grid header rows.
- [Aggregate queries](/BifrostQL/guides/aggregate-queries/)
- [Data workbench overview](/BifrostQL/guides/workbench/)
