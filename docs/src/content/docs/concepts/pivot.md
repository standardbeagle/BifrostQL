---
title: "Server-Side Pivot and Cross-Tab Queries"
description: "Run server-side pivot and cross-tab queries through the generated table pivot GraphQL field on all four SQL dialects, under a distinct-value cardinality cap."
---

BifrostQL generates a **server-side pivot (cross-tab)** query for every table through a
schema-generated root field, `<table>Pivot`. The server does all of the
pivoting — it discovers the distinct pivot-column values, generates a
parameterized cross-tab, and returns the result. The client never cross-tabs
fetched rows itself.

Under the hood the field routes through
[`PivotSqlGenerator.GeneratePivot`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/QueryModel/PivotSqlGenerator.cs),
which uses SQL Server's native `PIVOT` operator where available and a portable
`CASE WHEN` cross-tab on every other dialect. The field name and aggregate-function
enum come from a single source of truth,
[`PivotSurface`](https://github.com/standardbeagle/BifrostQL/blob/main/src/BifrostQL.Core/Schema/PivotSurface.cs),
so the SDL and the SQL can never disagree.

## The `<table>Pivot` field

```graphql
type Query {
  ordersPivot(
    rowKeys: [ordersEnum!]!
    pivotColumn: ordersEnum!
    valueColumn: ordersEnum!
    aggregate: PivotAggregate! = count
    filter: TableFilterordersInput
  ): JSON!
}

enum PivotAggregate { count sum avg min max }
```

- **`rowKeys`** — one or more columns that form the stable left-hand row group.
- **`pivotColumn`** — the column whose distinct values become output columns.
- **`valueColumn`** — the column the aggregate is computed over.
- **`aggregate`** — `count` (default), `sum`, `avg`, `min`, or `max`.
- **`filter`** — the same filter input the row query accepts.

All four arguments that name a column are **schema-derived enums**
(`<table>Enum`), so a caller can never inject an arbitrary identifier into
the generated SQL.

### Example

Cross-tab total order `amount` by `region` (rows) against `quarter` (columns):

```graphql
{
  ordersPivot(
    rowKeys: [region]
    pivotColumn: quarter
    valueColumn: amount
    aggregate: sum
  )
}
```

The result is a `JSON!` scalar: a list of row objects, each carrying its row-key
values plus one field per distinct pivot value.

## Security: distinct-value discovery is filtered

The subtle correctness property of a server-side pivot is that the *column
headers* — the distinct pivot-column values — must not leak rows the caller
cannot see. The resolver runs the tenant-isolation and soft-delete filter
transformers **before** the distinct-value discovery query, so a cross-tenant
pivot value never appears as an output column. This is the same fail-closed
filter path the row and [aggregate](/BifrostQL/guides/aggregate-queries/) queries
use. All identifiers are schema-derived and all values are parameterized.

## Cardinality cap

A pivot expands one output column per distinct pivot value, so an unbounded
pivot column would generate a runaway-wide result set and SQL statement. The
resolver caps the number of distinct pivot-column values
(`PivotSurface.DefaultMaxPivotColumns`, default **100**). Above the cap it
**errors with steering** rather than truncating — a truncated pivot would
silently drop columns and misrepresent the data. Add a `filter` to narrow the
pivot column, or choose a lower-cardinality column.

`NULL` pivot values render as their own explicitly labeled category, distinct
from the empty string.

## Related

- [Aggregate queries](/BifrostQL/guides/aggregate-queries/) — the `GROUP BY`
  companion surface for one row per group.
- [Pivot UI](/BifrostQL/guides/workbench/pivot-ui/) — the drag-and-drop pivot
  designer in the desktop workbench that drives this field.
