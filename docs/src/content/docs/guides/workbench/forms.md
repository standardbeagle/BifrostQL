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

## The server-side form and view builders

BifrostQL.Core ships a second way to put a form over a table, for pages that run
without JavaScript. `BifrostFormBuilder` (`src/BifrostQL.Core/Forms/`) plus
`ListViewBuilder` and `DetailViewBuilder` (`src/BifrostQL.Core/Views/`) each
return a **string of HTML-encoded markup** generated from the `DbModel`:

- `GenerateForm(table, FormMode.Insert|Update|Delete, values, errors, foreignKeyOptions)`
  emits a `<form method="POST">` whose action is
  `{basePath}/form/{table}/{mode}[/{id}]`, adding
  `enctype="multipart/form-data"` when the table has a binary column.
- `GenerateListView(table, records, PaginationInfo, sort, dir, search)` emits a
  search form, a sortable table, and pagination links.
- `GenerateDetailView(table, recordData)` emits a `<dl>` field list plus
  edit/delete/list action links.

`BifrostFormValidator.Validate(...)` returns a `ValidationResult` of
`ValidationError(fieldName, message)` you feed back into `GenerateForm` to
re-render with per-field errors.

### One model, two form stacks

The server builders and the form runner above read the **same `DbModel` and the
same schema metadata**. Server-rendered inputs take their HTML validation
attributes from `ValidationRules.ForColumn` — the `min`, `max`, `step`,
`minlength`, `maxlength`, `pattern`, `pattern-message`, `input-type`, and
`required` metadata keys. A column marked `populate` is dropped from the form
entirely. File inputs follow the `file` and `storage` keys.

They diverge above that shared floor. The form runner builds its own
`FormDefinition` JSON in the browser
(`src/BifrostQL.UI/frontend/src/forms/form-state.ts`), reads schema over the
Photino `get-builder-schema` bridge or the GraphQL `_dbSchema` field, and keeps
definitions in browser local storage. The C# builders emit finished HTML and
read neither that JSON nor that storage. Treat them as two independent lineages
over one model: reach for the server builders when you want a server-rendered
CRUD page in an existing ASP.NET app, and for the form runner when you want the
desktop or SPA surface this page documents.

### Wiring the builders

The builders carry **no DI registration and no mapped endpoint**. You construct
them with an `IDbModel` and route them yourself.
`examples/forms-sample/Program.cs` is the complete worked wiring: minimal-API
routes for list, view, insert, update, and delete, each returning
`Results.Content(html, "text/html")`.

## Related

- [Tabular reports](/BifrostQL/guides/workbench/printable-tables/) — print the
  records a form maintains.
- [Saved queries](/BifrostQL/guides/workbench/saved-queries/) — a form's source
  can be a saved query.
- [ER diagram](/BifrostQL/guides/workbench/erd/) — see which relationships a
  subform can bind to.
- [Data workbench overview](/BifrostQL/guides/workbench/)
