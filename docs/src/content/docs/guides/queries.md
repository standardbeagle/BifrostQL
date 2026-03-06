---
title: Queries
description: Filtering, sorting, and pagination in BifrostQL.
---

Every table query in BifrostQL returns paged results. The table name is the query field, and results live inside a `data` array.

## Basic query

```graphql
{
  users(limit: 10, offset: 0) {
    data {
      userId
      name
      email
    }
  }
}
```

The `limit` and `offset` parameters control pagination. Without `limit`, the default page size from your configuration applies.

## Filtering

Filters use Directus-style syntax. Pass a `filter` argument with column names and operators:

```graphql
{
  orders(filter: { status: { _eq: "shipped" }, total: { _gt: 100 } }) {
    data {
      orderId
      total
      status
    }
  }
}
```

### Filter operators

| Operator | Description | Example |
|----------|-------------|---------|
| `_eq` | Equal | `{ status: { _eq: "active" } }` |
| `_neq` | Not equal | `{ status: { _neq: "deleted" } }` |
| `_gt` | Greater than | `{ price: { _gt: 50 } }` |
| `_gte` | Greater than or equal | `{ price: { _gte: 50 } }` |
| `_lt` | Less than | `{ price: { _lt: 100 } }` |
| `_lte` | Less than or equal | `{ price: { _lte: 100 } }` |
| `_in` | In list | `{ status: { _in: ["active", "pending"] } }` |
| `_nin` | Not in list | `{ status: { _nin: ["deleted"] } }` |
| `_contains` | Contains substring | `{ name: { _contains: "smith" } }` |
| `_ncontains` | Does not contain | `{ name: { _ncontains: "test" } }` |
| `_starts_with` | Starts with | `{ name: { _starts_with: "A" } }` |
| `_ends_with` | Ends with | `{ email: { _ends_with: ".com" } }` |
| `_null` | Is null | `{ deletedAt: { _null: true } }` |
| `_nnull` | Is not null | `{ email: { _nnull: true } }` |
| `_between` | Between two values | `{ price: { _between: [10, 50] } }` |

### Combining filters

Multiple fields at the same level are combined with AND:

```graphql
{
  products(filter: { price: { _gt: 10 }, category: { _eq: "electronics" } }) {
    data { productId name price }
  }
}
```

Use `_and` and `_or` for explicit boolean logic:

```graphql
{
  products(filter: {
    _or: [
      { price: { _lt: 10 } },
      { category: { _eq: "sale" } }
    ]
  }) {
    data { productId name price category }
  }
}
```

## Sorting

Sort uses an enum list. Each column has `_asc` and `_desc` variants:

```graphql
{
  products(sort: [price_asc, name_desc]) {
    data {
      productId
      name
      price
    }
  }
}
```

Sort fields are applied in order. The first field is the primary sort, subsequent fields break ties.

## Pagination

Combine `limit` and `offset` for pagination:

```graphql
# Page 1
{ users(limit: 20, offset: 0) { data { userId name } } }

# Page 2
{ users(limit: 20, offset: 20) { data { userId name } } }
```

The `total` field on the page type returns the count of all matching rows (before pagination):

```graphql
{
  users(limit: 20, offset: 0) {
    total
    data {
      userId
      name
    }
  }
}
```

## Selecting fields

Request only the fields you need. BifrostQL generates SQL that selects only the requested columns, so narrower queries are genuinely faster at the database level.

```graphql
{
  users {
    data {
      userId
      email
    }
  }
}
```

This produces `SELECT [userId], [email] FROM [users]` -- not `SELECT *`.
