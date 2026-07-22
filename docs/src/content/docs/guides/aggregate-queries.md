---
title: "Aggregate Queries (GROUP BY)"
description: "Group rows and compute count/sum/avg/min/max through the schema-generated <table>Aggregate field — server-side aggregation with the same tenant-isolation and soft-delete filters as row queries."
---

Every table gets a **grouped-aggregate** root field, `<table>Aggregate`, that
runs `GROUP BY` with `count`, `sum`, `avg`, `min`, and `max` on the server and
returns one row per group. It is the analytical companion to the row query:
totals, breakdowns, and count cards come from here, not from summing fetched
page rows on the client.

The field name, the groupable-column set, and the value operations all derive
from a single source of truth,
[`AggregateSurface`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/Schema/AggregateSurface.cs),
and execute through
[`AggregateTableResolver`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/Resolvers/AggregateTableResolver.cs).

## The `<table>Aggregate` field

```graphql
type Query {
  ordersAggregate(
    filter: TableFilterordersInput
    groupBy: [ordersEnum!]
  ): [orders_aggregate!]!
}

type orders_aggregate {
  # one nullable group-key field per visible column
  status: String
  region: String
  # count of rows in the group
  _count: Int!
  # value op groups (present only when the table has numeric columns)
  _sum: orders_aggregateFields
  _avg: orders_aggregateFields
  _min: orders_aggregateFields
  _max: orders_aggregateFields
}

type orders_aggregateFields {
  amount: Float
  quantity: Float
}
```

- **`groupBy`** takes a list of the schema-derived column enum
  (`<table>Enum`), so a caller can never pass a name that is not a real,
  visible column.
- **`_count`** is always available.
- **`_sum` / `_avg` / `_min` / `_max`** are only emitted when the table has at
  least one numeric column; each resolves to an object with one `Float` field
  per numeric column. (`min`/`max` are restricted to numeric columns in this
  release; date/string extrema are a deferred extension.)

### Example

Count orders and total their amount, broken down by status and region:

```graphql
{
  ordersAggregate(groupBy: [status, region]) {
    status
    region
    _count
    _sum { amount }
    _avg { amount }
  }
}
```

## Security: filters apply before grouping

Rows excluded by the tenant-isolation and soft-delete filter transformers are
excluded from the aggregate. The resolver applies the same
transformer-derived filters as the row query **before** the `GROUP BY`, so a
count or sum can never include rows the caller cannot read. Column-level read
guards (`IColumnReadGuard`) are enforced against both `groupBy` columns and
`_sum`/`_avg`/`_min`/`_max` value columns — a denied column cannot be read
through the aggregate surface.

All identifiers are schema-derived and all values are parameterized; no
user-supplied string is ever concatenated into the generated SQL. The surface
is covered across SQL Server, PostgreSQL, MySQL, and SQLite.

## Related

- [Pivot / cross-tab queries](/BifrostQL/concepts/pivot/) — one output column
  per distinct value instead of one row per group.
- [Chart panel](/BifrostQL/guides/workbench/charts/) and
  [grid grouping](/BifrostQL/guides/workbench/grouping/) — desktop workbench
  surfaces that consume this field.
