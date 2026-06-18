---
title: EAV Attributes & the _meta Field
description: Surface Entity-Attribute-Value side tables (like WordPress wp_postmeta) as a single _meta JSON field on the parent type — metadata-driven, no name guessing, returned as a real nested object.
---

Many schemas keep a row's dynamic, open-ended attributes in a key/value **side table** — the classic Entity-Attribute-Value (EAV) pattern. WordPress's `wp_postmeta` is the canonical example: one row per attribute, joined back to the post by `post_id`.

BifrostQL collapses that side table into a single `_meta` field on the parent type, returned as a **real nested JSON object** — not an escaped string you have to parse twice.

```sql
-- wp_postmeta
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

`_meta` is BifrostQL's registered `JSON` scalar, so the attributes arrive as one structured object on the parent row.

## Configuration (metadata-driven only)

Declare the link on the **meta** table with `eav-*` metadata. `eav-parent` must name the parent table **exactly** — there is no name-based detection or prefix heuristic, so nothing is surfaced by accident.

```text
dbo.wp_postmeta {
  eav-parent: wp_posts;
  eav-fk: post_id;
  eav-key: meta_key;
  eav-value: meta_value
}
```

| Key | Meaning |
|-----|---------|
| `eav-parent` | The parent table this side table describes (exact name) |
| `eav-fk` | The foreign-key column on the meta table pointing at the parent |
| `eav-key` | The column holding the attribute name |
| `eav-value` | The column holding the attribute value |

## How it works

At model build, `EavConfigCollector` reads the `eav-*` metadata. `EavMetaProvider` (an `IComputedColumnProvider`) then synthesizes the read-only `_meta` field on each EAV-parent table through the standard [provider computed-column](/BifrostQL/concepts/computed-columns-and-validation/) pipeline: per row it reads the parent's primary key, queries the meta table, and returns the attributes as JSON that the `JSON` scalar serializes into a real object.

## Scope & limits

- **Read-only**, single-primary-key parents only (composite PK resolves to `null`).
- One query per parent row (N+1) — fine for detail views; batch upstream for large lists.
- `_meta` is **opaque to SQL** — you can't filter or sort *by* an attribute. Richer attribute querying is intentionally out of scope; if you need it, model the attributes as real columns or extend the GraphQL surface. The `_meta` object is enough for the read-and-display case it targets.
