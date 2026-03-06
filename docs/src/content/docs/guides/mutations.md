---
title: Mutations
description: Insert, update, upsert, and delete operations in BifrostQL.
---

BifrostQL generates four mutation operations for every table that has a primary key: insert, update, upsert, and delete.

## Insert

Pass field values via the `data` argument. Auto-increment columns are excluded from the input type.

```graphql
mutation {
  insert_products(data: { name: "Widget", price: 9.99, category: "hardware" }) {
    productId
    name
    price
  }
}
```

The mutation returns the inserted row, including any server-generated values like auto-increment IDs and defaults.

## Update

Update requires all non-nullable columns in the input, not just the fields being changed. The primary key identifies which row to update.

```graphql
mutation {
  update_products(data: {
    productId: 42,
    name: "Updated Widget",
    price: 12.99,
    category: "hardware"
  }) {
    productId
    name
    price
  }
}
```

BifrostQL uses the primary key value(s) in the input to locate the row. If the row doesn't exist, no update occurs.

## Upsert

Upsert inserts the row if the primary key doesn't exist, or updates it if it does. Same input shape as update:

```graphql
mutation {
  upsert_products(data: {
    productId: 42,
    name: "Widget",
    price: 9.99,
    category: "hardware"
  }) {
    productId
    name
    price
  }
}
```

On SQL Server, this uses `MERGE`. On PostgreSQL, `INSERT ... ON CONFLICT DO UPDATE`. On MySQL, `INSERT ... ON DUPLICATE KEY UPDATE`.

## Delete

Delete uses a filter (not a data input) and returns the count of deleted rows:

```graphql
mutation {
  delete_products(filter: { productId: { _eq: 42 } })
}
```

The filter syntax is identical to query filters. You can delete multiple rows by broadening the filter.

## Required fields

BifrostQL determines required fields from database nullability:

- **Insert**: All non-nullable columns without defaults are required. Auto-increment columns are excluded.
- **Update**: All non-nullable columns are required (the full row must be provided). The primary key is required to identify the target row.
- **Upsert**: Same as update.
- **Delete**: The filter argument is required.

## Mutations with modules

The module system can transform mutations before they hit the database.

**Soft delete** converts DELETE operations into UPDATE operations that set a timestamp column:

```
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id; }"
```

With this configured, `delete_orders` becomes an UPDATE that sets `deleted_at = NOW()` and `deleted_by_user_id` to the current user.

**Audit columns** auto-populate fields like `created_by` and `updated_on` from the authenticated user context:

```
"dbo.*.createdOn { populate: created-on; update: none; }"
"dbo.*.updatedOn { populate: updated-on; update: none; }"
"dbo.*.createdBy { populate: created-by; update: none; }"
```

These columns are populated automatically during insert and update. The `update: none` setting makes them read-only in the GraphQL input types.

## Return values

Insert, update, and upsert return the affected row. You can select any fields from the return type:

```graphql
mutation {
  insert_users(data: { name: "Alice", email: "alice@example.com" }) {
    userId
    name
    createdOn
  }
}
```

Delete returns an integer count.
