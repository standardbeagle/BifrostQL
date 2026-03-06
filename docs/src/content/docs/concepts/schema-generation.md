---
title: Schema Generation
description: How BifrostQL reads your database and builds a GraphQL schema.
---

BifrostQL generates its entire GraphQL schema from your database metadata. There are no mapping files, no code generation steps, and no manual type definitions. The database is the single source of truth.

## How it works

At startup, BifrostQL:

1. Connects to your database using the configured provider
2. Reads all tables, columns, primary keys, and foreign key relationships
3. Maps SQL types to GraphQL types using the dialect's type mapper
4. Generates query fields (one per table, paged), mutation fields (insert, update, upsert, delete per table), and input types
5. Applies metadata rules to hide tables/columns, configure modules, and adjust behavior
6. Caches the schema for the lifetime of the application

When you add a table or column to the database, restart the application and the corresponding GraphQL field appears with the correct type and nullability.

## Type mapping

Each SQL dialect defines its own type mapper. The general mapping follows this pattern:

| SQL Type | GraphQL Type |
|----------|-------------|
| `int`, `bigint`, `smallint` | `Int` |
| `decimal`, `numeric`, `money` | `Decimal` |
| `float`, `real`, `double` | `Float` |
| `varchar`, `nvarchar`, `text` | `String` |
| `bit`, `boolean`, `tinyint(1)` | `Boolean` |
| `datetime`, `timestamp`, `date` | `DateTime` |
| `uniqueidentifier`, `uuid` | `String` |

Columns that are non-nullable in the database produce non-nullable (`!`) GraphQL fields. Primary key columns generate the appropriate ID-style handling in mutations.

## Generated query fields

For a table named `orders`, BifrostQL generates:

```graphql
type Query {
  orders(
    limit: Int
    offset: Int
    sort: [OrderSort]
    filter: OrderFilter
  ): OrderPage
}

type OrderPage {
  data: [Order]
  total: Int
}
```

The sort enum contains one entry per column in both ascending and descending variants:

```graphql
enum OrderSort {
  orderId_asc
  orderId_desc
  customerId_asc
  customerId_desc
  total_asc
  total_desc
}
```

## Generated mutation fields

For each table with a primary key, BifrostQL generates four mutation operations:

```graphql
type Mutation {
  insert_orders(data: OrderInsertInput!): Order
  update_orders(data: OrderUpdateInput!): Order
  upsert_orders(data: OrderUpsertInput!): Order
  delete_orders(filter: OrderFilter!): Int
}
```

Insert inputs exclude auto-increment columns. Update inputs require all non-nullable columns (not just the ones being changed). Delete returns the count of affected rows.

## Metadata overrides

You can control schema generation with metadata rules in your configuration:

```json
{
  "BifrostQL": {
    "Metadata": [
      "dbo.sys* { visibility: hidden; }",
      "dbo.*.__* { visibility: hidden; }",
      "dbo.*.password_hash { visibility: hidden; }"
    ]
  }
}
```

Hidden tables and columns are excluded from the generated schema entirely. See [Configuration](/BifrostQL/reference/configuration/) for the full metadata rule syntax.

## Schema caching

The schema is cached per endpoint path using `PathCache<Inputs>`. This means the schema is built once and reused for all requests to the same path. To pick up database changes, restart the application.

## Naming conventions

BifrostQL preserves your database naming conventions in the GraphQL schema. Table names become query fields (with optional de-pluralization). Column names become field names as-is. This means your GraphQL queries match your database structure directly -- no surprises, no name translation layer.

If you need de-pluralized table names (e.g., `users` table exposed as `user` query), set the `de-pluralize` metadata property:

```
"dbo.* { de-pluralize: true; }"
```
