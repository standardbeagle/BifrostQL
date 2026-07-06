---
title: "App Metadata Overlay"
description: "The app-metadata overlay describes how SPA and React Native clients should present BifrostQL entities — labels, forms, grids, and relationships — as a standalone JSON layer that coexists with schema metadata."
---

The **app-metadata overlay** is a presentation layer that describes *how an application client should render* the entities BifrostQL exposes: human-readable labels, form widgets, list grids, saved views, and relationship presentation. It is standalone JSON, served to SPA and React Native clients, and it sits on top of — without replacing — the existing schema-metadata system.

## Why a separate layer

BifrostQL already has a **schema-metadata** system: the `dbo.table { key: value }` rule grammar that controls *how the GraphQL API itself behaves* — tenant filters, soft-delete, EAV flattening, policy enforcement. That metadata is consumed server-side, on the query and mutation paths.

The overlay answers a different question: *how should a client present this entity to a user?* That is a UI concern, not an API-behavior concern, so it lives in its own layer with its own contract.

| | Schema metadata | App-metadata overlay |
|---|---|---|
| **Purpose** | Controls API behavior (filtering, mutations, security) | Controls client presentation (labels, forms, grids) |
| **Grammar** | `dbo.table { key: value }` rule strings | Standalone camelCase JSON |
| **Consumed by** | BifrostQL server (query/mutation pipeline) | SPA / React Native clients |
| **Model type** | `DbModel`, `MetadataKeys` | `AppMetadataModel`, `EntityMetadata` |
| **Loader** | `MetadataLoader`, `IMetadataSource` | `AppMetadataLoader`, `IAppMetadataSource` |
| **Keyed by** | Qualified table name | Qualified table name |

Both layers are keyed by qualified table name (e.g. `dbo.users`), so the overlay aligns with `DbModel` tables without modifying them.

## How the two coexist

The overlay is a **deliberately separate, coexisting pipeline**. It is loaded independently, exposed independently, and is never merged into the schema-metadata system:

- `AddBifrostAppMetadata(...)` registers the overlay as its own singleton `AppMetadataModel`. It is purely additive — it touches no service registered by `AddBifrostQL`, and can be added before, after, or omitted entirely with no effect on the schema-metadata pipeline.
- The overlay deliberately does **not** reuse the `{ }` rule-delimiter grammar. It is plain JSON.
- Schema-metadata rules and overlay entries can both describe the same table; neither overrides the other because they govern different concerns.

Because the layers are independent, adding an overlay never changes existing API behavior, and all existing schema-metadata tests stay green.

## The overlay shape

`AppMetadataModel` is a pure data aggregate — no database or GraphQL dependency — describing entities keyed by qualified table name:

- **`EntityMetadata`** — label, icon, display fields, navigation placement, plus nested `Fields`, `Grid`, and `Relationships`.
- **`FieldMetadata`** — widget hint, validation rule, visibility, read-only, help text, layout group.
- **`GridPresetMetadata`** — default columns, filters, sort, named `SavedViews`, and bulk actions.
- **`RelationshipMetadata`** — target entity (by qualified table name), relationship kind (`foreignKeySelector`, `childCollection`, `nestedPanel`), foreign-key field, and display columns.

## Loading the overlay

Overlay entries come from one or more `IAppMetadataSource` instances, merged in priority order by `AppMetadataLoader`:

- **`FileAppMetadataSource`** — reads the overlay from a JSON file on disk (low priority). A missing file yields an empty overlay.
- **`DatabaseAppMetadataSource`** — reads the overlay from a database table, one entity per row (higher priority).
- **`CompositeAppMetadataSource`** — merges several sources; when more than one supplies the same qualified table name, the higher-priority source wins.

```csharp
services.AddBifrostAppMetadata(new IAppMetadataSource[]
{
    new FileAppMetadataSource("app-metadata.json"),          // priority 0
    new DatabaseAppMetadataSource(connectionString),         // priority 100
});
```

## Serving the overlay to clients

The overlay is exposed to SPA and React Native clients through a GET endpoint, following the same middleware pattern BifrostQL uses for its info endpoint:

```csharp
app.UseBifrostAppMetadata();                    // GET /_app-metadata
// or with options
app.UseBifrostAppMetadata(o => o.Path = "/meta");
```

The endpoint serves the overlay as the **stable camelCase JSON contract** defined by `AppMetadataJson` — the same portable, RN-friendly shape the model serializes to. When no overlay is registered, the endpoint returns an empty overlay rather than 404, so clients always receive the stable contract.

## Example: a Membership Manager

A Membership Manager application tracks `members`, `households`, `dues`, and `events`. With the overlay, every presentation concern is data:

- **Labels and navigation** — `members` → "Members", icon `person`, nav placement `main`.
- **Forms** — each field carries a widget (`text`, `select`, `datepicker`), a layout group, and optional validation/help text, so a client renders the form generically with no entity-specific form code.
- **Grids** — each entity's `GridPresetMetadata` gives default columns, sort, filters, and saved views, so the list view needs no hardcoded grid.
- **Relationships** — `members → households` (foreign-key selector), `members → dues` (child collection), `events → households` — every target resolves to another overlay entity by qualified table name.

The result: a client can describe and render all four entities purely from the overlay JSON, with no hardcoded forms or grids.

:::note[Status]
The overlay JSON contract and the server-side pipeline (`AddBifrostAppMetadata`, `UseBifrostAppMetadata`) are implemented. The shipped edit-db editor does not yet consume the overlay for presentation — it introspects the `_dbSchema` GraphQL type instead. Rendering purely from the overlay is the intended end state for overlay-aware clients, not how the current editor works.
:::

## Reference

### File format

The overlay JSON is a single top-level object with one key — `entities` — whose value is a map from **qualified table name** (e.g. `main.members`) to an `EntityMetadata` document. Every key is camelCase. Every field is optional; a missing field falls back to a sensible default.

```jsonc
{
  "entities": {
    "<schema>.<table>": {
      "label":         "Display name",                  // string, default = table name
      "icon":          "person",                        // material/lucide icon hint
      "navPlacement":  "main" | "secondary" | "hidden", // SPA nav slot
      "displayFields": [ "first_name", "last_name" ],   // for relationship pickers
      "fields": {
        "<col>": {
          "widget":     "text" | "select" | "datepicker" | "fk-lookup" | "…",
          "group":      "Identity",                     // form-section label
          "validation": "email" | "url" | "…",
          "visible":    true,                           // default true
          "readOnly":   false,
          "helpText":   "Free-form help text"
        }
      },
      "grid": {
        "defaultColumns": [ "first_name", "last_name", "email" ],
        "defaultSort":    [ "last_name asc" ],
        "defaultFilters": [ "status = active" ],
        "savedViews": {
          "<viewId>": { "name": "Display label", "filters": [ "status = inactive" ] }
        },
        "bulkActions": [ "export", "email" ]
      },
      "relationships": {
        "<relName>": {
          "label":         "Household",
          "targetEntity":  "main.households",
          "kind":          "foreignKeySelector" | "childCollection" | "nestedPanel",
          "foreignKey":    "household_id",
          "displayColumns": [ "name" ]
        }
      }
    }
  }
}
```

### Endpoint contract

`GET /_app-metadata` returns the loaded overlay verbatim in the shape above. The path is configurable via `BifrostAppMetadataOptions.Path`; the response is always JSON (`application/json; charset=utf-8`). When no overlay sources are registered, the endpoint returns `{ "entities": {} }` — never 404 — so clients can treat the contract as stable.

### Reference sample

A complete real-world overlay lives at [`samples/HostedSpa/membership-manager.appmetadata.json`](https://github.com/standardbeagle/BifrostQL/blob/main/samples/HostedSpa/membership-manager.appmetadata.json). It exercises every documented key — form widgets, grouped fields, hidden admin-only columns, grid saved views, foreign-key and child-collection relationships — for the Membership Manager sample.
