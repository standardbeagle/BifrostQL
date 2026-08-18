---
title: "Database Forms and Subforms"
description: "Database forms and subforms: run a saved form over live data with record navigation, CRUD through the grid's mutation hooks, and composite-key-safe binding."
---

Database forms and subforms run in the workbench's **form runner**, which turns a
saved form definition into a working data-entry surface: it renders one record at a time, navigates between records, and
creates, edits, and deletes rows through the same mutation hooks and validation
the grid uses. Forms persist as [saved objects](/BifrostQL/concepts/saved-objects/)
(`type: form`).

## Running a form

- **Navigation** — first / previous / next / new, plus jump-to by primary key.
  Paged fetches go through the query-builder helpers and are **composite-PK
  safe** (`row-id.ts`), so multi-column and BigInt keys work end to end.
- **CRUD** — create, update, and delete through the existing edit-db mutation
  hooks; field validation runs before submit, and server errors surface
  per-field where possible. A new-record insert lands on the created row using
  the primary key returned by the mutation.
- **Widgets** — each field's input is chosen from its type plus the
  app-metadata `FieldMetadata` widget hint; `readOnly` and `visible` are
  respected.

Everything runs through the editor's `useFetcher()` seam — no second HTTP
client — so the form is covered by the HTTP ↔ binary transport toggle.

## Subforms (related records)

A form can embed **subforms**: child grids or stacked forms bound to the parent
record through a schema-known relationship.

- The child grid is filtered by the parent primary key → foreign key, using the
  query-builder helpers.
- **Add-child** prefills the foreign-key columns from the parent and hides them
  from input; delete or detach follows the relationship kind — a foreign-key
  child is deleted, a many-to-many link is detached through the existing M2M
  panel.
- Navigating the parent refreshes its children; an unsaved-child guard fires
  before you navigate away.

### Composite foreign keys

A relationship join that reads only the first source/destination column assumes
a single-column foreign key. Subforms support composite foreign keys when both
sides expose their full column lists; where they do not, the subform surfaces an
explicit **"unsupported relationship"** state rather than silently guessing with
`column[0]`.

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/)
- [Tabular reports](/BifrostQL/guides/workbench/printable-tables/)
