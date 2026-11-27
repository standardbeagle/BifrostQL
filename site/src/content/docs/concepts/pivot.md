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
which emits one portable `CASE WHEN` cross-tab on all four dialects, SQL Server
included. The field name and aggregate-function
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

The result is a `JSON!` scalar: `{ pivotColumn, rowKeys, columns, rows, truncated }`.
Each entry in `rows` carries its row-key values plus a `cells` object with one
field per distinct pivot value.

## Row ceiling

The cross-tab is a `GROUP BY` over the row keys, so `rowKeys: [id]` yields one
output row per table row. The pivot obeys the same `max-query-rows` ceiling as
every other read: it fetches at most that many rows, ordered by the row keys.
The last of them is a sentinel, so the pivot returns at most one row fewer than
the ceiling and sets `truncated: true` when more rows exist. Narrow the scope
with `filter` to see the rest.

## Security: distinct-value discovery is filtered

The subtle correctness property of a server-side pivot is that the *column
headers* — the distinct pivot-column values — must not leak rows the caller
cannot see. The resolver runs the tenant-isolation and soft-delete filter
transformers **before** the distinct-value discovery query, so a cross-tenant
pivot value never appears as an output column. This is the same fail-closed
filter path the row and [aggregate](/BifrostQL/guides/aggregate-queries/) queries
use. Table and column identifiers are schema-derived, and filter values are
parameterized. Pivot *values* are data, not schema: on every dialect each one is
bound as a parameter in its own `CASE WHEN` arm and never appears in the SQL
text; a NULL pivot value is matched with `IS NULL`, and the null label only names
its key in the result. Output columns are aliased by position (`__pivot_0`,
`__pivot_1`, …), so no label ever becomes an identifier. An empty label, a label
of any length, and labels that differ only in accents, kana type or width each
get their own column, matched under the pivot column's own collation. Labels
equal ignoring case (`A` and `a`, or a NULL beside a value equal to the null
label) would name one result key twice, so the pivot is refused with an error
naming the label.

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
