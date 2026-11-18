# EAV Attributes (`_meta`)

Surfaces Entity-Attribute-Value (EAV) tables — a key/value side table holding a row's
dynamic attributes (e.g. WordPress `wp_postmeta`) — on the parent type as a single
`_meta` field.

```sql
-- EAV meta table (e.g. wp_postmeta)
| post_id | meta_key | meta_value |
|---------|----------|------------|
| 1       | color    | red        |
| 1       | size     | L          |
```

```graphql
{ posts { data { id _meta } } }
```
```json
{ "id": 1, "_meta": { "color": "red", "size": "L" } }
```

`_meta` is the registered `JSON` scalar — a row's attributes as one nested object.

## Configuration (metadata-driven only)

Declare the link on the **meta** table with `eav-*` metadata. `eav-parent` must name the
parent table **exactly** — there is no name-based detection or inference.

```
"dbo.wp_postmeta { eav-parent: wp_posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }"
```

## How it works

- `EavConfigCollector` reads the `eav-*` metadata at model build into `model.EavConfigs`.
- `EavMetaProvider` (an `IComputedColumnProvider`, name `eav-meta`) synthesizes the
  read-only `_meta` field on each EAV-parent table via the standard
  provider-computed-column pipeline as an `IBatchComputedColumnProvider`. Per result page,
  it collects the parents' single primary keys, queries the meta table once
  (`SELECT fk,key,value WHERE fk IN (...)` windowed per parent, 500 keys per query), and returns each row's
  attributes as a JSON object string that the `JSON` scalar serializes into a real object.

## Scope / limits

- Read-only. Single-PK parents only (composite PK → `null`).
- One meta-table query per result page, not per parent row. Each parent is windowed in SQL
  (`ROW_NUMBER` per FK, ordered by key) to `max-query-rows - 1` attributes plus a sentinel
  row. `_meta` reserves no key; the sibling `_metaTruncated: Boolean` field
  (`EavMetaTruncatedProvider`, `GROUP BY fk HAVING COUNT(*) > page` under the same guards)
  is true for a parent the window cut.
- No SQL-level filter/sort **by** an attribute — `_meta` is an opaque object. Richer
  attribute querying is intentionally out of scope.
