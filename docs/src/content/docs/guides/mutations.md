---
title: "GraphQL Insert, Update, Upsert, Delete"
description: "Write rows through generated GraphQL mutations: single, batch, collection-diff delta, explicit-ops graph save, filtered set-update, and nested TreeSync reconcile."
---

BifrostQL exposes one mutation field per table that has a primary key, and is deliberately generous about save shapes: the common recipes each have a first-class, performant form.

| Recipe | Shape | When to use |
|--------|-------|-------------|
| Single row | `<t>(insert:/update:/upsert:/delete:)` | One row, one verb |
| Entity list + ops | `<t>_batch(actions: [...])` | Mixed per-row operations, one transaction, [set-based fast path](/reference/bulk-batch-performance/) at scale |
| Collection diff | `<t>(delta: { inserted, updated, deleted })` | A grid save or sync loop's computed diff, sent as one document — flattens onto the batch pipeline (same transaction, caps, duplicate policy, fast path) |
| Explicit-ops graph | `<t>(save: { ..., _op, children })` | A nested document where every node states its own operation; unlisted children are untouched |
| Inferred reconcile | `<t>(sync: { ... })` | Make the database match the submitted tree: ops inferred from key presence, orphans deleted |
| Filtered set-update | `<t>(updateWhere: { set, where })` | `UPDATE ... SET ... WHERE` semantics — opt-in per table, capped |

## Insert

Pass field values through the table's `insert` argument. Auto-increment and computed columns are excluded from the insert input type. The mutation returns the inserted identity value when the dialect can report one.

```graphql
mutation {
  products(insert: { name: "Widget", price: 9.99, category: "hardware" })
}
```

## Update

Update uses the table's `update` argument. The primary key can be supplied either inside the update input or through `_primaryKey` for composite-key workflows. Only non-key fields in the input are updated. The mutation returns the primary-key value for the updated row.

```graphql
mutation {
  products(update: {
    productId: 42,
    name: "Updated Widget",
    price: 12.99
  })
}
```

BifrostQL uses the primary key value(s) to locate the row. If the row does not exist, no update occurs.

## Upsert

Upsert inserts the row if the primary key does not exist, or updates it if it does. It uses the table's `upsert` argument:

```graphql
mutation {
  products(upsert: {
    productId: 42,
    name: "Widget",
    price: 9.99,
    category: "hardware"
  })
}
```

On SQL Server, this uses `MERGE`. On PostgreSQL, `INSERT ... ON CONFLICT DO UPDATE`. On MySQL, `INSERT ... ON DUPLICATE KEY UPDATE`.

## Delete

Delete uses the table's `delete` argument. The delete input makes primary-key fields required and non-key fields optional, so primary-key deletes are the normal path. It returns the count of deleted rows.

```graphql
mutation {
  products(delete: { productId: 42 })
}
```

For composite primary keys, pass all key values in declaration order through `_primaryKey`:

```graphql
mutation {
  orderItems(delete: {}, _primaryKey: ["42", "7"])
}
```

## Collection-diff saves (`delta`)

A grid editor or sync loop that computes a diff sends it as one document:

```graphql
mutation {
  products(delta: {
    inserted: [ { name: "New", price: 5.0 } ],
    updated:  [ { productId: 42, price: 12.99 } ],
    deleted:  [ { productId: 17 } ]
  })
}
```

Sections apply in `inserted` → `updated` → `deleted` order inside ONE transaction — a failure anywhere applies nothing. The reply is the total affected count. Because the delta flattens onto the batch pipeline, `batch-max-size`, `batch-duplicate-policy`, and the set-based bulk fast path all apply unchanged.

## Explicit-ops graph saves (`save`)

Where `sync` *infers* operations by diffing against database state (and deletes orphans), `save` executes exactly what the document says — nothing more:

```graphql
mutation {
  blogs(save: {
    id: 1, name: "renamed",
    posts: [
      { title: "brand new" },              # no key → insert (FK auto-wired to the parent)
      { id: 11, title: "edited" },         # key present → update
      { id: 12, _op: delete }              # explicit delete needs only the key
    ]
  })
}
```

Rules: a node's `_op` (`insert`/`update`/`delete`) wins; absent, a node carrying its full primary key updates and one without inserts. Unlisted children are untouched — `save` never infers deletes. Root delete is legal and returns the submitted key. The whole graph is one transaction; each node runs the full transformer chain (a soft-delete table's `_op: delete` becomes the soft-delete UPDATE), and a fresh parent's generated key flows to its children's foreign keys. A `save` skips `sync`'s current-state load entirely, so it is also the faster graph write.

## Filtered set-updates (`updateWhere`)

SQL's `UPDATE ... SET ... WHERE` as a mutation — **opt-in per table**:

```
"dbo.products { filtered-update: enabled; filtered-update-max-affected: 500 }"
```

```graphql
mutation {
  products(updateWhere: {
    set: { discontinued: true },
    where: { category: { _eq: "legacy" }, price: { _lt: 1.0 } }
  })
}
```

The `where` grammar is the table's read-side filter type; the reply is the affected-row count. Guard rails, all fail-closed: the argument only exists on opted-in tables; your filter ANDs into (never replaces) tenant/policy/soft-delete scope; filter columns clear the same column-permission guards as reads (a denied column errors, it is never stripped); an empty `where` is refused; a `COUNT` precheck inside the update's own transaction enforces `filtered-update-max-affected` (default 100) with rollback; and tables with approval/history/CDC hooks, state machines, or concurrency tokens refuse the set-based form outright — use `_batch` there.

## Batch mutations

Every table also gets a `<table>_batch` field. Each action can contain one operation, and the field returns the number of operations applied.

```graphql
mutation {
  products_batch(actions: [
    { insert: { name: "Widget", price: 9.99 } },
    { update: { productId: 42, price: 12.99 } },
    { delete: { productId: 43 } }
  ])
}
```

## Required fields

BifrostQL determines required fields from database nullability:

- **Insert**: All non-nullable columns without defaults are required. Auto-increment columns are excluded.
- **Update**: The primary key is required to identify the target row. Non-key fields are optional, but at least one changed field must be provided.
- **Upsert**: Same input shape as update, with identity keys optional when the database can generate them.
- **Delete**: Primary-key fields are required unless `_primaryKey` supplies them.

## Mutations with modules

The module system can transform mutations before they hit the database.

**Soft delete** converts DELETE operations into UPDATE operations that set a timestamp column:

```
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id; }"
```

With this configured, `orders(delete: ...)` becomes an UPDATE that sets `deleted_at = NOW()` and `deleted_by_user_id` to the current user.

**Audit columns** auto-populate fields like `created_by` and `updated_on` from the authenticated user context:

```
"dbo.*.createdOn { populate: created-on; update: none; }"
"dbo.*.updatedOn { populate: updated-on; update: none; }"
"dbo.*.createdBy { populate: created-by; update: none; }"
```

These columns are populated automatically during insert and update. The `update: none` setting makes them read-only in the GraphQL input types.

## Optimistic concurrency

Optimistic concurrency prevents *lost updates* — two clients read the same row, both edit it, and the second write silently overwrites the first. Opt in per table with the `concurrency-token` metadata, naming a column that versions the row:

```
"dbo.orders { concurrency-token: row_version }"
```

With this configured, every `update` **must carry the token value the row was read at**. BifrostQL ANDs `row_version = <the value you sent>` into the UPDATE's WHERE clause and, on success, advances the token in the same statement. Omitting the token (or sending `null`) is rejected — you cannot update a token-guarded row without declaring which version you are editing.

```graphql
mutation {
  orders(update: {
    id: 42,
    status: "shipped",
    row_version: 7   # the value you last read
  })
}
```

If nobody else has written the row, the token still matches, the row updates, and `row_version` advances (to `8`, or to a fresh timestamp for a datetime token). If another writer got there first, the stored token has already moved, the guarded WHERE matches **zero rows**, and the write is rejected rather than silently lost — the row keeps the other writer's value.

### The conflict shape

A stale-token write does not silently no-op and does not surface a generic error. It fails with a stable, branchable shape: a `BifrostExecutionError` whose **`ErrorCode` is `CONFLICT`**. Detect it and prompt the user to reload and retry:

> Update of 'dbo.orders' was rejected: the concurrency token no longer matches — the row was modified or removed since it was read. Reload and retry.

The message is deliberately generic: it discloses no current column values, so a losing writer learns only *that* it lost, never *what* the winning value was.

### Supported token types

| Token type | On each write |
|------------|---------------|
| Numeric (`int`, `bigint`, `decimal`, …) | Incremented by 1 (checked arithmetic — an at-max token fails cleanly rather than wrapping) |
| Datetime (`datetime`, `datetimeoffset`) | Restamped to the current UTC time |

Database-managed version columns (SQL Server `rowversion`, PostgreSQL `xmin`) are **not yet supported** — they are rejected with a clear error rather than silently left un-bumped. Supporting them is a documented follow-up.

### Batch upsert refuses token tables

The single-statement batch upsert path (`ON CONFLICT DO UPDATE` / `MERGE`) always writes — it cannot express a "fail if the token moved" WHERE. Rather than silently bypass the guard, BifrostQL **refuses** to write a concurrency-token table through that path (also a `CONFLICT`). A stale token can therefore never degrade into an INSERT that resurrects a row you believed you were updating. Use a plain `update` (or a batch `update` action) for token-guarded tables.

## Return values

Table mutation fields return scalar values, not row objects:

- **Insert**: inserted identity value when available
- **Update**: primary-key value
- **Upsert**: inserted identity or updated primary-key value, depending on path
- **Delete**: affected row count
- **Batch**: applied action count
