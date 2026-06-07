---
title: Lookup-table enums
description: Mark a lookup table with enum metadata and BifrostQL emits a GraphQL enum type; columns that reference it become typed, filterable, and writable as that enum.
---

A lookup table — a small table whose rows are a fixed set of allowed values (`status`, `priority`, `category`) — can be surfaced as a real GraphQL enum instead of a free-form string. Mark the table with `enum:` metadata and BifrostQL emits a GraphQL enum type named `{Table}Values`. Every column that references that table is then typed as the enum, can be filtered by enum name, and is written by enum name on insert/update/upsert.

This is implemented for **value-valued** columns (Approach A): the referencing column stores the value *string*, so there is no id translation. See [Not supported yet](#not-supported-yet) for the FK-by-id case.

## Configuration

Enum behavior is driven entirely by schema metadata.

### Mark the lookup table

```text
dbo.status { enum: true }            # auto value column (first non-PK string column)
dbo.status { enum: code }            # explicit value column
dbo.status { enum: code:display }    # value column + label column
```

- `enum: true` auto-detects the value column: the first non-primary-key string-typed column (`varchar`, `nvarchar`, `char`, `nchar`, `text`, `ntext`).
- `enum: code` names the value column explicitly.
- `enum: code:display` names the value column and an optional label column.

The emitted enum type is named after the table's GraphQL name with a `Values` suffix — e.g. table `status` yields the enum type `statusValues`.

### Force a column onto an enum

When a referencing column has no foreign key (or you want to override detection), point it at the enum table explicitly:

```text
dbo.orders.status_col { enum-ref: dbo.status }
```

`enum-ref` makes `status_col` render as the `status` enum even without an FK. The optional `schema.` prefix is stripped, so `dbo.status` and `status` resolve the same.

## How a column becomes an enum

A column resolves to an enum table when, in priority order:

1. it carries `enum-ref` metadata naming an enum table, **or**
2. it has a foreign key whose target table is an enum table **and** whose targeted column is that enum table's resolved value column.

The enum's members are the **sanitized distinct values** of the value column, read once at schema-build time. Sanitization upper-cases the value and replaces invalid characters with underscores to form a valid GraphQL enum name — e.g. `on hold` → `ON_HOLD`, `high-priority` → `HIGH_PRIORITY`. Values that cannot be represented (empty after sanitization) are dropped. A table whose value set sanitizes to nothing does not participate as an enum and its columns stay plain scalars.

## Read, filter, and write

The mapping between the stored database value and the GraphQL enum name is bidirectional and applied at every boundary:

- **Read** — the stored value maps to its enum name in the response. `"active"` is returned as `active`'s declared member.
- **Filter** — `status: { _eq: ACTIVE }` translates `ACTIVE` to its stored value before the WHERE clause is built. Both scalar operands (`_eq`, `_neq`, …) and collection operands (`_in`) are translated.
- **Write** — on insert, update, and upsert the supplied enum name is written as its stored value.

This works on nested (joined) selections too — an enum column read through a relationship is mapped the same way as a top-level column.

## FK navigation is suppressed

When a column becomes an enum *via its foreign key*, the column already carries the value, so the redundant single-link navigation field of the same name is **not** emitted. This avoids a field collision between the enum column and its parent navigation; the enum scalar is the single surface for that relationship.

## Drift behavior

Enum membership is captured at schema-build time. It does not refresh on its own. If a stored value is not a declared member — for example a value inserted into the lookup table after the schema was built, or a value that does not sanitize to a valid name — then:

- **On read**, that field resolves to `null` and a structured warning is logged. The rest of the row is unaffected.
- **On filter or write**, an unknown enum name is a validation / mutation error.

To pick up lookup-table data changes, refresh membership by reconnecting / resetting the schema cache.

## Security and scope

Enum membership is built **per connection** and shared across all requests served by that schema. Because the enum type is baked into the shared schema, membership **cannot be tenant-scoped** — every tenant sees the same enum members.

What *is* applied at load time:

- **Soft-delete** — if the lookup table declares a soft-delete column, soft-deleted rows are excluded from enum membership (the load adds `WHERE <soft-delete-column> IS NULL`). Soft-delete is a context-free predicate, so it is intrinsic to membership.

What is **not** scoped here:

- **Row-level tenant filtering** still applies to actual data queries through the normal filter pipeline. Only enum *membership* is non-tenant-scoped — the rows your queries return are filtered exactly as before. Do not rely on enum membership to hide tenant-specific values.

## Not supported yet

**FK-by-id enums.** A referencing column that stores the lookup table's primary-key integer (rather than the value string) is not mapped to an enum today. Only value-valued columns — where the referencing column holds the value string and the FK targets the enum table's value column — are wired (Approach A).
