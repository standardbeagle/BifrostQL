---
title: Mutations
description: Insert, update, upsert, and delete operations in BifrostQL.
---

BifrostQL exposes one mutation field per table that has a primary key. The table field accepts one of four operation arguments: `insert`, `update`, `upsert`, or `delete`. It also exposes a `<table>_batch` field for applying several operations in one request.

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

## Return values

Table mutation fields return scalar values, not row objects:

- **Insert**: inserted identity value when available
- **Update**: primary-key value
- **Upsert**: inserted identity or updated primary-key value, depending on path
- **Delete**: affected row count
- **Batch**: applied action count
