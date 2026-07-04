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
| `_nstarts_with` | Does not start with | `{ name: { _nstarts_with: "A" } }` |
| `_ends_with` | Ends with | `{ email: { _ends_with: ".com" } }` |
| `_nends_with` | Does not end with | `{ email: { _nends_with: ".test" } }` |
| `_like` | SQL LIKE pattern | `{ name: { _like: "A%" } }` |
| `_nlike` | Negated SQL LIKE | `{ name: { _nlike: "A%" } }` |
| `_null` | Is null (`true`) / is not null (`false`) | `{ deletedAt: { _null: true } }` |
| `_between` | Between two values | `{ price: { _between: [10, 50] } }` |
| `_nbetween` | Not between two values | `{ price: { _nbetween: [10, 50] } }` |

### Combining filters

Multiple fields at the same level are combined with AND:

```graphql
{
  products(filter: { price: { _gt: 10 }, category: { _eq: "electronics" } }) {
    data { productId name price }
  }
}
```

Use `and` and `or` for explicit boolean logic:

```graphql
{
  products(filter: {
    or: [
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

## Aggregates

Roll up related rows inline with `_agg`. It computes an aggregate over a child collection per parent row, so you can return "order count" or "average score" alongside the parent without a second round-trip:

```graphql
{
  workshops {
    data {
      id
      sessionCount: _agg(value: { sessions: id } operation: count)
      avgScore:     _agg(value: { sessions: { entry: score } } operation: avg)
    }
  }
}
```

- `value` names the path to aggregate — `{ joinTable: column }`, nested as deep as the relationship goes.
- `operation` is one of `count`, `sum`, `avg`, `min`, `max`.
- Alias each `_agg` (e.g. `sessionCount:`) when you request more than one.

`_agg` runs across all four engines — SQL Server, PostgreSQL, MySQL, and SQLite — through the [dialect layer](/BifrostQL/reference/dialects/). Aggregating a column requires a nested foreign-key path; a bare-column aggregate raises a clear error.

For cross-tab / matrix output, see [Pivot / Cross-Tab](/BifrostQL/concepts/pivot/).
