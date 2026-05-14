---
title: Joins
description: Automatic and explicit table joins in BifrostQL.
---

BifrostQL exposes relationships directly on generated table types. Foreign-key and name-based relationships become fields on the row, and many-to-many relationships become list fields.

## Automatic joins

When a table has a column whose name matches another table's single-column primary key, BifrostQL infers the join automatically. No configuration required.

Given these tables:

```sql
CREATE TABLE customers (
  customerId INT PRIMARY KEY,
  name NVARCHAR(100)
);

CREATE TABLE orders (
  orderId INT PRIMARY KEY,
  customerId INT,  -- matches customers.customerId
  total DECIMAL(10,2)
);
```

You can join from orders to customers:

```graphql
{
  orders {
    data {
      orderId
      total
      customers {
        name
      }
    }
  }
}
```

BifrostQL sees that `orders.customerId` matches `customers.customerId` and generates the appropriate JOIN clause.

## Direct child collections

Parent rows expose child collections when BifrostQL can infer the relationship. Child collection fields accept a `filter` argument:

```graphql
{
  customers {
    data {
      customerId
      name
      orders(filter: { total: { _gt: 100 } }) {
        orderId
        total
      }
    }
  }
}
```

## Composite key joins

For tables linked by multiple columns, BifrostQL supports composite key matching. If both columns match by name and both tables use a composite primary key, the join is automatic.

Composite relationships are exposed as normal nested fields:

```graphql
{
  orderItems {
    data {
      orderId
      lineNumber
      inventory {
        quantity
      }
    }
  }
}
```

## Nested joins

Joins can nest arbitrarily deep:

```graphql
{
  orders {
    data {
      orderId
      customers {
        name
        addresses {
          street
          city
        }
      }
    }
  }
}
```

This walks from `orders` to `customers` to `addresses` in a single request. BifrostQL generates the SQL needed to resolve this in one round-trip.

## Many-to-many joins

Many-to-many relationships are detected from junction tables or declared with `many-to-many` metadata. The target table is exposed as a list field:

```graphql
{
  posts {
    data {
      postId
      title
      tags {
        tagId
        name
      }
    }
  }
}
```

You can declare one manually when automatic detection is not enough:

```
"dbo.posts { many-to-many: tags:post_tags; }"
```

## Application schema relationships

Prefer real database foreign keys where possible. Known application schemas such as WordPress can also inject synthetic foreign keys during schema detection, so legacy databases without declared constraints can still expose direct relationship fields.

## Join behavior

- Joins produce nested results, not flattened rows.
- Top-level table queries are paged; relationship fields return nested objects or lists.
- Child collection fields support filters.
- Automatic join detection uses database foreign keys and name-based conventions. Disable it with `auto-join: false` metadata.
- Generated `_join` / `_single` containers are controlled by `dynamic-joins`; prefer direct relationship fields in application queries.

## Controlling join behavior

Use metadata rules to disable automatic relationship inference or generated dynamic join containers:

```
"dbo.sensitive_table { auto-join: false; }"
"dbo.audit_log { dynamic-joins: false; }"
```

`auto-join: false` prevents BifrostQL from inferring joins based on column names. `dynamic-joins: false` removes `_join` and `_single` containers from generated table types.
