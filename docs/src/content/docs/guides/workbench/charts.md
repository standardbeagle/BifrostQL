---
title: "Chart Panel"
description: "Bind bar, line, pie, and area charts to server aggregate queries — every rendered value comes from a GROUP BY query, not a client-side sum — with theme-aware palettes, a high-cardinality guard, and explicit NULL and empty states."
---

The **chart panel** (recharts) renders bar, line, pie, and area charts bound to
the server [aggregate](/BifrostQL/guides/aggregate-queries/) surface. A chart's
configuration saves as a [saved object](/BifrostQL/concepts/saved-objects/) of
`type: query`.

## How a chart is built

A chart configuration is JSON: `{ source, dimensions, measures: [{ col, op }],
chartType, filter, sort, limit }`. The panel translates it into a **grouped
aggregate GraphQL query** through the query-builder helpers (schema-derived
names only — no user string is interpolated into GraphQL) and maps dimension
values to the category axis and measures to series.

## Server-side values only

Every value a chart renders comes from a server grouped-aggregate query; the
panel performs **no client-side summation** over fetched page rows. Clicking
**Visualize** on the grid with an active column filter produces a chart whose
aggregate query carries that same filter, so the rendered totals match the
filtered SQL.

## Edge handling

- **High cardinality.** A dimension with thousands of distinct values does not
  freeze the UI — the documented guard (a cap with an "other" bucket, or an
  explicit error) fires instead of rendering thousands of categories.
- **NULL dimensions** render with an explicit label, distinct from the empty
  string.
- **Empty result** renders a friendly empty state, not a blank canvas.

## Theming

Chart colors resolve from theme tokens, so charts render correctly in both light
and dark themes rather than from hard-coded hex values.

## Related

- [Dashboards](/BifrostQL/guides/workbench/dashboards/) — reuse a chart as a
  read-only dashboard tile.
- [Aggregate queries](/BifrostQL/guides/aggregate-queries/)
