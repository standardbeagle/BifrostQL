---
title: "Pivot UI"
description: "A drag-and-drop pivot designer — Rows, Columns, and Values field wells — over the server-side <table>Pivot GraphQL surface, with debounced re-queries, a surfaced cardinality-guard error, and table-or-saved-query sources."
---

The **pivot UI** is a drag-and-drop cross-tab designer over the server
[pivot](/BifrostQL/concepts/pivot/) surface. The server does all cross-tabbing;
the UI never groups fetched rows itself. A pivot configuration saves as a
[saved object](/BifrostQL/concepts/saved-objects/).

## Field wells

- **Rows** — one or more row-key columns (a functional multi-field well; all
  selected keys go in one request).
- **Columns** — a single pivot column.
- **Values** — a column plus an aggregate op (`count`, `sum`, `avg`, `min`,
  `max`).

The field list is the schema-derived columns for the chosen source. Moving a
field between wells triggers exactly **one** re-query after a debounce interval
(a debounce regression is caught by test).

## Sources, results, and errors

- The same configuration works against a **table** source and a **saved-query**
  source, producing identical results for equivalent data.
- The result grid renders dynamic pivot-value columns against a fixed row-key
  column group; `NULL` pivot values appear as an explicitly labeled category.
- When a later pivot request returns the server
  [cardinality-guard](/BifrostQL/concepts/pivot/#cardinality-cap) error, the UI
  surfaces a message naming the offending column and suggesting a filter — the
  previous grid stays visible rather than blanking.

Export uses the shared [export](/BifrostQL/guides/workbench/export/) utility.

## Related

- [Pivot / cross-tab queries](/BifrostQL/concepts/pivot/) — the GraphQL surface
  this UI drives.
- [Data workbench overview](/BifrostQL/guides/workbench/)
