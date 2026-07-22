---
title: "Grid Grouping"
description: "Group the data grid by one or more columns with server-computed group counts and totals — never a client-side sum over a partial page — with URL-persisted grouping state and per-group member expansion."
---

The data grid can **group by** one or more columns, with group header rows whose
counts and totals are computed **on the server**, not by summing the rows
currently on the page. This keeps totals correct on large tables.

## Two-tier fetch

- **Collapsed view** issues one grouped
  [aggregate](/BifrostQL/guides/aggregate-queries/) query returning group keys, a
  count, and configured sums — paginated over **groups**, not member rows.
- **Expanding a group** fetches only that group's member rows, with the group
  key merged into the active filter through the query-builder helpers. A `NULL`
  group key is its own labeled group, fetched with a `_null` filter (not
  `_eq: null`).

No total anywhere is computed client-side; TanStack's client aggregation is not
used for header values.

## URL is the source of truth

Grouping state round-trips through a `gb` URL parameter, mirroring the `cf`
column-filter parameter: setting grouping updates the URL, and loading that URL
restores the same grouping. There is no parallel component state driving the
query, so **switching tables clears grouping** — no `gb` value from the previous
table leaks into the new table's query.

With an active column filter and grouping, the filter composes first and then
the grouping, so totals match the filtered SQL.

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/)
- [Aggregate queries](/BifrostQL/guides/aggregate-queries/)
