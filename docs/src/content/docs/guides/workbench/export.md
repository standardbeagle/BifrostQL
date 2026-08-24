---
title: "CSV and JSON Data Export"
description: "CSV and JSON data export from every result surface: the full result set across all pages, RFC 4180 quoting, BigInt-safe JSON, and an optional Excel BOM."
---

CSV and JSON data export reaches every result surface in the workbench — the
edit-db grid, the [SQL console](/BifrostQL/guides/workbench/sql-editor/), and the
[report runner](/BifrostQL/guides/workbench/printable-tables/) — all backed by
**one shared utility**
(`examples/edit-db/src/lib/export.ts`). There is no second export implementation
in the repo.

## What export guarantees

- **Full result set, not the visible page.** A grid export with an active column
  filter and sort pages through the fetcher and exports every matching row
  across all pages — the exported row count equals the server's total count for
  that filter.
- **RFC4180-correct CSV.** A value containing a comma, a double quote, or a
  newline is quoted exactly per the spec, and `NULL` is emitted distinguishably
  from the empty string.
- **Excel BOM option.** With the BOM option enabled, the UTF-8 byte-order mark
  is the first bytes of the file.
- **BigInt-safe JSON.** A BigInt primary-key value round-trips without precision
  loss (carried as a string, never a `Number`-coerced value), and dates keep a
  documented, stable format.
- **Row cap with confirm.** Exporting above the row cap prompts for confirmation
  first; cancelling mid-export stops paging and leaves no partial file.

## Delivery

In the browser, export downloads a Blob via a transient anchor. On the desktop
shell, the editor's exports (grid toolbar and the table list's Download
actions) route through the native `save-file` bridge: the OS save dialog picks
the destination, a cancelled dialog writes nothing, and the shell reports the
saved path. Hosts embedding the editor elsewhere can supply their own saver via
`<Editor saveFile={...}>`.

## Related

- [Grid grouping](/BifrostQL/guides/workbench/grouping/) — grouping composes
  with export; the filter applies to both.
- [Tabular reports](/BifrostQL/guides/workbench/printable-tables/) — report CSV
  rides this same utility.
- [Composite-PK compliance](/BifrostQL/concepts/schema-generation/) — why
  BigInt and composite keys are carried as strings.
- [Data workbench overview](/BifrostQL/guides/workbench/)
