---
title: "EAV Tables as a GraphQL JSON _meta Field"
description: "Surface entity-attribute-value tables such as WordPress wp_postmeta as one _meta JSON field on the parent GraphQL type. Metadata-driven, with no name guessing."
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

At model build, `EavConfigCollector` reads the `eav-*` metadata. `EavMetaProvider` (an `IBatchComputedColumnProvider`) then synthesizes the read-only `_meta` field on each EAV-parent table through the standard [provider computed-column](/BifrostQL/concepts/computed-columns-and-validation/) pipeline: for each result page it collects the parents' primary keys, reads their attribute rows in one `WHERE fk IN (...)` query, and returns each row's attributes as JSON that the `JSON` scalar serializes into a real object.

## Scope & limits

- **Read-only**, single-primary-key parents only (composite PK resolves to `null`).
- One meta-table query per result page (500 parent keys per query), not one per parent row. Each parent returns at most `max-query-rows - 1` attributes (9,999 by default), the first in attribute-key order. A parent with more has `_metaTruncated: true`, a `Boolean` field beside `_meta` (false when `_meta` is complete); the rest are not read. `_meta` reserves no key, so every attribute key reads back as data. `_metaTruncated` counts the parent's attribute rows in its own query, so selecting both reads the meta table twice per page.
- `_meta` is **opaque to SQL** — you can't filter or sort *by* an attribute. Richer attribute querying is intentionally out of scope; if you need it, model the attributes as real columns or extend the GraphQL surface. The `_meta` object is enough for the read-and-display case it targets.
