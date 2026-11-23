---
title: "Saved Queries, Forms, Reports, Dashboards"
description: "One saved-object store keeps user-authored queries, forms, reports, and dashboards behind a /_saved-objects CRUD endpoint, apart from schema and app metadata."
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

**Which endpoint.** `BifrostSavedObjectsOptions.GraphQlEndpoint` names the registered GraphQL endpoint the mount serves; null selects the single registered endpoint. `UseBifrostSavedObjects()` refuses to start when that choice is not one registered endpoint: several registered with none named, or a path no endpoint is registered under.

**Authentication.** The mount takes its auth requirement from that endpoint. It requires a signed-in caller unless the endpoint sets `DisableAuth`, and always requires one when no endpoint is registered. `RequireAuth` overrides the inherited value. All anonymous callers share one partition, so an anonymous mount is a choice for a trusted-loopback desktop host; set `RequireAuth = true` to keep the store closed in front of an anonymous GraphQL endpoint. Mount it after `UseBifrostQL()` / `UseBifrostEndpoints()`.

**Owner partitions.** A signed-in caller's partition is derived from their `user_id` plus their tenant, read from the claim the endpoint's model names with `tenant-context-key` (default `tenant_id`), so two tenants whose identity providers issue the same subject stay apart. A caller with no `user_id`, or whose tenant claim holds several values, has no stable owner and gets `401`. If the endpoint's model cannot be loaded (its database is unreachable), a signed-in request gets `503` with a fixed message; the detail goes to the server log.

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
rather than silently coerced. The type is accepted only as its name: a numeric
value such as `"type": 7` is a `400`, never an out-of-range enum.

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

Two caps bound what one caller can store:

- **Request body** — `BifrostSavedObjectsOptions.MaxRequestBodyBytes` (default
  512 KiB, twice the 256 KiB definition cap). The middleware counts the bytes it
  actually receives, so a chunked body with no `Content-Length` is cut off at the
  cap with `413`.
- **Objects per owner** — both stores refuse a *create* past
  `SavedObjectJson.DefaultMaxObjectsPerOwner` (1000, a constructor argument on
  `FileSavedObjectStore` and `DbSavedObjectStore`) with `507`. Updates and
  deletes of existing objects still work at the cap.

Two storage backends sit behind one `ISavedObjectStore` interface:

- **Desktop** — a local JSON file store under the profile directory.
- **Hosted** — a database-table backend
  ([`DbSavedObjectStore`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/SavedObjects/DbSavedObjectStore.cs)),
  with parameterized SQL across all dialects. No host wires it by default: pass
  a `DbSavedObjectStore` to `AddBifrostSavedObjects` to use it. Its key columns
  are bounded text (`VARCHAR(128)`, `NVARCHAR(128)` on SQL Server), so an `id`
  is at most 128 characters on both backends. A list returns at most
  `maxListRows` objects (default 100,000). A longer list fails instead of
  being cut short.

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
