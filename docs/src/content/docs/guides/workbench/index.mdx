---
title: "Headless CRUD Admin Over Your Database"
description: "A headless CRUD admin over your database: Access-style saved queries, forms and reports, a SQL editor and ER diagram, plus charts, pivots and dashboards."
---

The BifrostQL desktop app (the [Desktop Navigator](/BifrostQL/guides/desktop-app/))
is a **headless CRUD admin over your database**. Point it at a connection string
and you get browse, search, edit, and report surfaces without writing a screen.
Every pane reads the live schema at runtime, so a new column shows up on the next
connect and your codebase stays free of scaffolded UI.

The workbench blends three familiar tools over your existing database:

- **Access parity** — [saved queries](/BifrostQL/guides/workbench/saved-queries/)
  from the visual designer, [forms](/BifrostQL/guides/workbench/forms/) with
  subforms, and [tabular reports](/BifrostQL/guides/workbench/printable-tables/).
- **DBeaver parity** — a [CodeMirror SQL editor](/BifrostQL/guides/workbench/sql-editor/)
  with schema autocomplete, a read-only
  [ER diagram](/BifrostQL/guides/workbench/erd/), and
  [export everywhere](/BifrostQL/guides/workbench/export/).
- **BI slice** — a [chart panel](/BifrostQL/guides/workbench/charts/), a
  [pivot UI](/BifrostQL/guides/workbench/pivot-ui/),
  [dashboards](/BifrostQL/guides/workbench/dashboards/), and server-backed
  [grid grouping](/BifrostQL/guides/workbench/grouping/).

## Two foundations everything sits on

**Server-side analytics.** Every total, series value, pivot cell, and group
header comes from a server query — the
[aggregate](/BifrostQL/guides/aggregate-queries/) and
[pivot](/BifrostQL/concepts/pivot/) GraphQL surfaces — never from summing a
fetched page in the browser. This keeps results correct on large tables and
keeps tenant-isolation and soft-delete filters in force.

**Saved objects.** Queries, forms, reports, and dashboards persist through the
unified [saved-object store](/BifrostQL/concepts/saved-objects/) — a dedicated
`/_saved-objects` pipeline, separate from schema metadata and from the
app-metadata overlay.

Every workbench data path routes through the editor's `useFetcher()` /
`QueryTransport` seam, so the HTTP ↔ binary transport toggle covers all of them.

## End-to-end: from a table to a dashboard

A minimal round-trip against a sample sales database, using the panes in order:

1. **Design a query.** In the visual query builder, pick `orders`, join
   `customers`, choose columns and a filter, and **save** it as a
   [saved query](/BifrostQL/guides/workbench/saved-queries/) named
   `Open orders`.
2. **Build a form.** Point the [form runner](/BifrostQL/guides/workbench/forms/)
   at `orders`, add an `order_lines` subform bound by the foreign key, and
   enter or edit records with child rows.
3. **Define a report.** Create a [report](/BifrostQL/guides/workbench/printable-tables/)
   over `Open orders` grouped by `region` with a `sum(amount)` subtotal band and
   grand total; print or export it.
4. **Chart it.** Click **Visualize** on the grid (or open the
   [chart panel](/BifrostQL/guides/workbench/charts/)) to bar-chart
   `sum(amount)` by `region` — the chart issues a server aggregate query, not a
   client sum.
5. **Assemble a dashboard.** Drop the chart, two count cards, and a table tile
   onto a [dashboard](/BifrostQL/guides/workbench/dashboards/); save it; reopen
   it — every tile restores and refetches its own data.

Each artifact you save is a [saved object](/BifrostQL/concepts/saved-objects/)
you can reopen, rename, or delete.

:::note[Runnable sample]
A packaged, downloadable end-to-end example project (seed database plus the
saved query, form, report, and dashboard definitions) is planned as a follow-up.
The walkthrough above is the current authoritative reference; each linked page
documents the exact surface it drives.
:::
