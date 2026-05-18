# AppMetadata overlay: dialect-coupling survey

**Purpose:** Catalogue every code path in the AppMetadata overlay that emits SQL or otherwise depends on a specific DB engine. Counterpart to `agg-dialect-survey.md`. Tracks portability for the standalone JSON overlay served at `GET /_app-metadata`.

## Trigger surface

| Component | Role |
|---|---|
| `AppMetadata/AppMetadataModel.cs` | Pure data aggregate: `EntityMetadata`, `FieldMetadata`, `GridPresetMetadata`, `RelationshipMetadata`, `SavedView`. No SQL, no DB. |
| `AppMetadata/AppMetadataJson.cs` | System.Text.Json serializer for the camelCase wire contract. No SQL. |
| `AppMetadata/AppMetadataLoader.cs` | Merges overlays from N `IAppMetadataSource` sources in priority order. No SQL. |
| `AppMetadata/IAppMetadataSource.cs` | Source abstraction. Two concrete: `FileAppMetadataSource`, `DatabaseAppMetadataSource`. Plus `CompositeAppMetadataSource`. |
| `Server/BifrostAppMetadataMiddleware.cs` | ASP.NET middleware serving the loaded overlay at `GET /_app-metadata`. No SQL. |
| `Server/Extensions.cs:AddBifrostAppMetadata` / `UseBifrostAppMetadata` | DI + pipeline registration. No SQL. |

## Direct SQL surface

Exactly one source emits SQL: `DatabaseAppMetadataSource`. It reads overlay rows from a database table.

| Read path | Notes |
|---|---|
| `SELECT … FROM <overlay table>` | The table name is configurable; columns are flat per-entity rows. The SQL is built with `dialect.EscapeIdentifier` and `dialect.TableReference`. |
| No writes | The overlay is read-only at the server. Updates happen out-of-band (file replace, table update by an admin tool). |

(If the source's SQL still has bracket-quoting leaks like `_agg` did before its refactor, they belong on the gap list. Walk the source before un-skipping any DatabaseAppMetadataSource tests.)

## Schema-metadata vs app-metadata coexistence

The overlay is **explicitly separate** from schema metadata (`MetadataKeys`, `IMetadataSource`):

- Loaded independently via `AddBifrostAppMetadata`.
- Never merged into the GraphQL schema generator output.
- Keyed by qualified table name (`<schema>.<table>`) so it aligns with `DbModel.Tables` without modifying them.

This is the right separation: the GraphQL pipeline never has to know about overlay data, so adding/removing the overlay is a purely additive concern.

## Endpoint contract

`GET /_app-metadata`:

- 200 OK with `application/json; charset=utf-8`.
- Body shape: `{ "entities": { "<schema>.<table>": <EntityMetadata>, … } }`.
- Empty overlay returns `{ "entities": {} }` (never 404).
- Configurable path via `BifrostAppMetadataOptions.Path`.

The shape is detailed in `docs/src/content/docs/concepts/app-metadata-overlay.md` (Reference section).

## Cross-dialect risks

1. **DatabaseAppMetadataSource SQL.** If the source emits SqlServer-flavored brackets, it breaks on Postgres/MySQL (audited but not run against real DBs yet).
2. **Column type assumptions.** The overlay table stores JSON blobs per entity. SqlServer `NVARCHAR(MAX)`, Postgres `JSONB` or `TEXT`, MySQL `LONGTEXT` or `JSON`. The loader must accept any of these.
3. **Table-name resolution.** The DatabaseAppMetadataSource expects a specific overlay-table name. Deployments that override the table name must propagate through both the loader options and any schema migrations.

## Test coverage today

| Test | Status |
|---|---|
| `AppMetadataModelTests` (if present) | unit coverage of model/JSON round-trip |
| **Any** integration test against the `GET /_app-metadata` endpoint with a real overlay loaded | **none** |
| **Any** integration test against `DatabaseAppMetadataSource` with a real overlay table | **none across all four engines** |

Worktrack task **"Integration tests for AppMetadata overlay (/_app-metadata endpoint)"** captures the gap.

## Refactor plan implied by this audit

1. Add a `BifrostQL.Server.Test` e2e that loads a sample overlay via `FileAppMetadataSource`, boots the middleware, hits `GET /_app-metadata`, and asserts the JSON matches the loaded model.
2. Add a parallel test wiring a `DatabaseAppMetadataSource` against Sqlite + a seeded overlay table. Then mirror to SqlServer/Postgres/MySQL.
3. Audit `DatabaseAppMetadataSource` SQL emission against `agg-dialect-survey.md`'s checklist — if any brackets are hardcoded, fix the same way `_agg` was.

## Open gaps

- Untested middleware path (the entire `GET /_app-metadata` response).
- Untested `DatabaseAppMetadataSource` SQL path on any engine.
- Overlay table schema not documented.
