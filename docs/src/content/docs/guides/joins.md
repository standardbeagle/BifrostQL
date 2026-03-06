---
title: Joins
description: Automatic and explicit table joins in BifrostQL.
---

BifrostQL adds a `__join` field to every table type. This field lets you traverse from any row to any other table, using either automatic key matching or explicit filter conditions.

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
      __join {
        customers {
          data {
            name
          }
        }
      }
    }
  }
}
```

BifrostQL sees that `orders.customerId` matches `customers.customerId` and generates the appropriate JOIN clause.

## Explicit joins with filters

When column names don't match, or you want a specific join condition, use filters inside the `__join`:

```graphql
{
  orders {
    data {
      orderId
      __join {
        users(filter: { userId: { _eq: 42 } }) {
          data {
            name
            email
          }
        }
      }
    }
  }
}
```

## Composite key joins

For tables linked by multiple columns, BifrostQL supports composite key matching. If both columns match by name and both tables use a composite primary key, the join is automatic.

For explicit composite joins, apply filters on each key column:

```graphql
{
  orderItems {
    data {
      orderId
      lineNumber
      __join {
        inventory(filter: {
          warehouseId: { _eq: 1 },
          productId: { _eq: 100 }
        }) {
          data {
            quantity
          }
        }
      }
    }
  }
}
```

## Nested joins

Joins can nest arbitrarily deep. Each `__join` opens a new level of traversal:

```graphql
{
  orders {
    data {
      orderId
      __join {
        customers {
          data {
            name
            __join {
              addresses {
                data {
                  street
                  city
                }
              }
            }
          }
        }
      }
    }
  }
}
```

This walks from `orders` to `customers` to `addresses` in a single query. BifrostQL generates the SQL joins to resolve this in one round-trip.

## Join behavior

- Joins produce sub-queries, not flat results. Each joined table returns its own paged `data` array.
- You can apply `filter`, `sort`, `limit`, and `offset` to joined tables the same way you would at the top level.
- Automatic join detection is based on column names matching primary key columns. Disable it per-table with `auto-join: false` in metadata.
- The `__join` field itself is always present on every table type. If there are no matching tables, it returns an empty result.

## Controlling join behavior

Use metadata rules to disable automatic joins or the `__join` field entirely:

```
"dbo.sensitive_table { auto-join: false; }"
"dbo.audit_log { dynamic-joins: false; }"
```

`auto-join: false` prevents BifrostQL from inferring joins based on column names. `dynamic-joins: false` removes the `__join` field from the table's GraphQL type entirely.
