---
title: "Saved Objects (Queries, Forms, Reports, Dashboards)"
description: "The unified saved-object store persists user-authored queries, forms, reports, and dashboards through a dedicated /_saved-objects CRUD endpoint — a separate pipeline from schema metadata and from the read-only app-metadata overlay."
---

The desktop workbench lets users author **queries, forms, reports, and
dashboards** and save them. All four ride one persisted model — the
**saved-object store** — instead of the fragmented storage they used before
(designer state in memory, forms in `localStorage`, saved views as read-only
metadata).

## Which pipeline saved objects ride

BifrostQL has three distinct, coexisting pipelines. Saved objects ride the
**third one, and are never merged into the other two**:

| Pipeline | Endpoint | Direction | Purpose |
|---|---|---|---|
| **Schema metadata** | (build-time, in `DbModel`) | server-side | Controls API behavior — tenant filters, soft-delete, EAV, encryption. Server-authoritative. |
| **App-metadata overlay** | `GET /_app-metadata` | read-only | camelCase JSON describing client presentation (labels, forms, grids). |
| **Saved-object store** | `/_saved-objects` (CRUD) | read/write | camelCase JSON holding user-authored query/form/report/dashboard definitions. |

The saved-object store is a **parallel, write-capable pipeline** that follows
the same camelCase-JSON conventions as the app-metadata overlay but is a
**separate endpoint**. Saved-object definitions are **never written into schema
metadata** — the two are separate by design (schema metadata is
server-authoritative and controls security; saved objects are user content).
A saved object's `definition` is opaque, type-specific JSON that the server
stores but does not interpret.

## The object model

```jsonc
{
  "id": "…",                 // stable across renames
  "type": "query",           // query | form | report | dashboard
  "name": "Quarterly sales",
  "folder": "reports/2026",  // optional, client-side organization; null = root
  "definition": { /* opaque, type-specific JSON */ },
  "version": 3               // optimistic-concurrency token
}
```

The `type` enum
([`SavedObjectType`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/SavedObjects/SavedObject.cs))
is `query`, `form`, `report`, or `dashboard`; an unrecognized value is rejected
rather than silently coerced.

`version` is an **optimistic-concurrency token**: a create carries `0` and the
store persists `1`; an update must carry the version it last read, and the
store writes `version + 1` only if it still matches — otherwise it returns a
`409` conflict. A concurrent create for the same `(type, id)` resolves the same
way: the loser gets a `409`, never an unhandled `500`.

## The `/_saved-objects` endpoint

[`BifrostSavedObjectsMiddleware`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Server/BifrostSavedObjectsMiddleware.cs)
serves a small REST surface (default base path `/_saved-objects`, configurable):

| Method + path | Behavior |
|---|---|
| `GET /_saved-objects` | list all (optional `?type=`) |
| `GET /_saved-objects/{type}` | list one type |
| `GET /_saved-objects/{type}/{id}` | fetch one (`404` if absent) |
| `PUT /_saved-objects/{type}/{id}` | create/update (`409` on stale version, `400` on invalid) |
| `DELETE /_saved-objects/{type}/{id}` | delete (`204`) |

Two storage backends sit behind one `ISavedObjectStore` interface:

- **Desktop** — a local JSON file store under the profile directory.
- **Hosted** — a database-table backend
  ([`DbSavedObjectStore`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/SavedObjects/DbSavedObjectStore.cs)),
  opt-in via configuration and off by default, with parameterized SQL across
  all dialects.

Clients reach the store through the same `useFetcher()` seam the rest of the
editor uses (`examples/edit-db/src/common/saved-objects.ts`), never a second
HTTP client.

## No cascade deletes

Deleting a saved object does **not** cascade to dashboards that reference it. A
dashboard tile whose referenced object was deleted renders a tile-level error
naming the missing id, while every other tile still loads — see
[dashboards](/BifrostQL/guides/workbench/dashboards/).

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/) — the surfaces that
  create and consume saved objects.
- [App-metadata overlay](/BifrostQL/concepts/app-metadata-overlay/) — the
  separate read-only presentation pipeline.
