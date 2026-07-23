---
title: Building SQL Expressions (SqlExpr)
description: Construct portable, parameterized column expressions with the public SqlExprBuilder — one fluent tree that lowers to correct SQL on SQL Server, PostgreSQL, MySQL, and SQLite. Node set, eager build-time validation, per-dialect support matrix, and a worked module example.
---

BifrostQL represents a column-level SQL expression — the kind a computed column or a custom
projection needs — as an immutable `SqlExpr` tree. The tree is **pure data**: it carries no dialect
knowledge and emits no SQL itself. Each shipped dialect *lowers* the same tree into its own
parameterized SQL, so you describe an expression **once** and it renders correctly on SQL Server,
PostgreSQL, MySQL, and SQLite.

This guide is for module authors. Everything here is **public** — the builder, the nodes, and the
lowering entry point are reachable from an external assembly with no `InternalsVisibleTo` grant from
`BifrostQL.Core`.

## The node set

`SqlExpr` is a closed hierarchy of immutable records. The full set:

| Node | Meaning |
|------|---------|
| `SqlExpr.Col` | A reference to a table column, validated against the table's real columns. |
| `SqlExpr.Param` | An explicitly bound parameter value with an optional provider DB type. |
| `SqlExpr.Lit` | A literal value — lowers to a bound parameter, never interpolated text. |
| `SqlExpr.Fn` | A function call over the closed allow-list (`UPPER`/`LOWER`/`LEN`/`ABS`/`ROUND`/`COALESCE`). |
| `SqlExpr.Case` | `CASE operand WHEN … THEN … [ELSE …] END`, with `SqlExpr.CaseBranch` per WHEN/THEN. |
| `SqlExpr.Cast` | A CAST to a portable `SqlExprType` (`Text` / `Int`); the storage type is chosen per dialect. |
| `SqlExpr.Concat` | String concatenation, rendered with each dialect's own concat form. |
| `SqlExpr.DateAdd` | Adds a signed amount of a `DateUnit` to a temporal source. |
| `SqlExpr.DateDiff` | The whole-unit difference `end - start` between two temporal expressions. |
| `SqlExpr.DatePart` | Extracts a single `DateUnit` field (year, month, …) from a temporal source as an integer. |
| `SqlExpr.JsonGet` | Extracts a scalar from a JSON source at a validated `JsonPath`. |

`DateUnit` is a closed enum (`Year`/`Month`/`Day`/`Hour`/`Minute`/`Second`); `SqlExprType` is a
closed enum (`Text`/`Int`). Neither a unit nor a cast target can reach the SQL text as free-form
input — each dialect maps them to its own keyword.

## The fluent builder

`SqlExprBuilder` is the public entry point. Bind it to the `IDbTable` your expression targets with
`SqlExprBuilder.For(table)`, then build:

```csharp
using BifrostQL.Core.QueryModel;

var b = SqlExprBuilder.For(table);

// UPPER(customerName) — column referenced by its GraphQL name
Expr label = b.Col("customerName").Upper();

// CASE UPPER(customerName) WHEN 'VIP' THEN 'STAR:' || customerName
//   ELSE CAST(ROUND(total, 2) AS <text>) END
Expr expr = b.Case(b.Col("customerName").Upper())
    .When(b.Lit("VIP"), b.Concat(b.Lit("STAR:"), b.Col("customerName")))
    .Else(b.Col("total").Round(b.Lit(2)).Cast(SqlExprType.Text))
    .End();
```

Every leaf constructor lives on the builder (`Col`, `Lit`, `Param`, `Fn`, `Concat`, `Cast`, `Case`,
`DateAdd`, `DateDiff`, `DatePart`, `JsonGet`) and each returns an `Expr` you can chain from —
`.Upper()`, `.Lower()`, `.Len()`, `.Abs()`, `.Round(digits)`, `.Coalesce(…)`, `.Concat(…)`,
`.Cast(type)`, `.DateAdd(unit, amount)`, `.DatePart(unit)`, `.JsonGet(…path)`. A `Case` is
accumulated through `CaseBuilder` (`.When(…)`, `.Else(…)`, `.End()`). The underlying tree is on
`Expr.Node`, and `Expr` implicitly converts to `SqlExpr`, so you can pass a built expression
straight into anything that takes a node.

### Eager, build-time validation

The builder validates **at the point of construction**, not at SQL-execution time. You get a public
`SqlExprBuildException` naming the offending symbol:

```csharp
b.Col("no_such_column");                 // throws: names "no_such_column" and the table
b.Fn("BOGUS_FN", b.Col("customerName")); // throws: names "BOGUS_FN"
b.Fn("UPPER", a, b);                     // throws: "UPPER expects exactly 1 argument(s)…"
```

- **Columns** resolve through the same authority the query path uses: GraphQL name first, then DB
  name (matching `ComputedColumnDefinition.ResolveDependencyColumn`). The resolved DB column name is
  stored in the node, so a tree built once lowers on every dialect without re-resolution.
- **Functions** are checked against `SqlExprFunctions` — the closed name + arity allow-list. Use
  `SqlExprFunctions.Names`, `SqlExprFunctions.IsKnown`, and `SqlExprFunctions.ValidateCall` to
  inspect it. `ROUND` requires the two-argument `(value, digits)` form and `COALESCE` requires at
  least two arguments, so a tree built once stays valid on all four dialects.

### JSON paths are validated too

`JsonGet` takes a `JsonPath` of identifier segments. Each segment must be a simple identifier
(`[A-Za-z_][A-Za-z0-9_]*`); anything else — quotes, brackets, dots, `$`, whitespace — is rejected at
construction, so client text can never inject SQL or JSON-path syntax:

```csharp
b.JsonGet(b.Col("payload"), "customer", "id");   // ok  -> $.customer.id
b.JsonGet(b.Col("payload"), "id'); DROP TABLE");  // throws at construction
```

## Per-dialect support matrix

Every node lowers to each engine's **native** form. `Col`, `Param`, `Lit`, `Case`, and every
allow-listed `Fn` are supported everywhere; the rows below capture where the spelling differs or a
node is genuinely unsupported.

| Node / case | SQL Server | PostgreSQL | MySQL | SQLite |
|-------------|-----------|-----------|-------|--------|
| `Fn("LEN", …)` | `LEN` | `LENGTH` | `LENGTH` | `LENGTH` |
| `Concat` | `a + b` | `a \|\| b` | `CONCAT(a, b)` | `a \|\| b` |
| `Cast(Text)` | `NVARCHAR(MAX)` | `TEXT` | `CHAR` | `TEXT` |
| `Cast(Int)` | `INT` | `INTEGER` | `SIGNED` | `INTEGER` |
| `DateAdd` | `DATEADD` | interval arithmetic (`INTERVAL`) | `DATE_ADD` | `datetime()` |
| `DatePart` | `DATEPART` | `EXTRACT` | `EXTRACT` | `strftime` (cast to integer) |
| `DateDiff` day/hour/minute/second | `DATEDIFF` | `FLOOR(EXTRACT(EPOCH …)/n)` | `TIMESTAMPDIFF` | `CAST(julianday-delta AS INTEGER)` |
| `DateDiff` **month / year** | supported | **NotSupported** | supported | **NotSupported** |
| `JsonGet` | `JSON_VALUE` | `->>` (jsonb) | `JSON_UNQUOTE(JSON_EXTRACT(…))` | `json_extract` |

A dialect that genuinely cannot lower a node throws `SqlExprLoweringNotSupportedException` — a typed
error naming **both** the node and the dialect — rather than emitting a silently-wrong
approximation. It derives from `BifrostExecutionError`, so it travels the normal error channel while
still being catchable as a distinct not-supported condition.

### DateDiff: cross-engine semantics differ

`DateDiff` does not mean the same arithmetic on every engine, so choose the unit deliberately:

- **SQL Server** (`DATEDIFF`) counts **boundary crossings** — `DATEDIFF(year, '2020-12-31',
  '2021-01-01')` is `1` even though only one day elapsed.
- **MySQL** (`TIMESTAMPDIFF`) counts **whole elapsed units** — the same two dates give `0` years
  because a full year has not elapsed.
- **PostgreSQL** and **SQLite** compute day/hour/minute/second from an epoch / Julian-day delta.
  Whole **month** and **year** differences cannot be counted exactly from such a delta (calendar
  boundaries vary), so they throw `SqlExprLoweringNotSupportedException` by design. Use
  `DatePart`-based arithmetic instead when you need month/year deltas on those engines.

### DateDiff: FLOOR vs truncation on negative intervals

For a **negative** difference (`end` earlier than `start`) the two epoch/Julian engines round
differently:

- **PostgreSQL** wraps the delta in `FLOOR(…)`, which rounds toward **negative infinity** — a
  partial negative day becomes the *more negative* whole number.
- **SQLite** wraps it in `CAST(… AS INTEGER)`, which **truncates toward zero** — the same partial
  negative day becomes the *less negative* whole number.

So a sub-unit negative interval can differ by one between PostgreSQL and SQLite. If a signed
difference feeds downstream comparisons, normalize with `ABS` or compare against an inclusive bound
rather than relying on identical rounding across engines.

### JsonGet on PostgreSQL requires a jsonb-typed source

PostgreSQL lowers `JsonGet` with the `->>` operator, which addresses **json / jsonb** values. Point
it at a column whose type is `json` or `jsonb` (declare the column type accordingly). Applied to a
plain `text` column, PostgreSQL raises an *operator does not exist* type error at execution — the
value is never silently coerced. The other engines extract from their own JSON/text representation
via `JSON_VALUE` (SQL Server), `JSON_UNQUOTE(JSON_EXTRACT(…))` (MySQL), and `json_extract` (SQLite).

## Lowering and the third-party dialect extension point

You lower a tree through the dialect's public entry point:

```csharp
var parameters = new SqlParameterCollection();
string sql = dialect.LowerExpression(expr, table, parameters);
// `sql` is a parameterized fragment; every Lit/Param value is bound into `parameters`.
```

`ISqlDialect.LowerExpression(SqlExpr, IDbTable, SqlParameterCollection)` is the **public lowering
surface** — the extension point a third-party dialect implements. The concrete Template-Method
implementation lives on `SqlDialectBase`: an exhaustive switch over the closed node set that binds
every value node as a parameter and validates every identifier. A new dialect derives from
`SqlDialectBase` and overrides its hooks:

- `MapFunctionName`, `RenderCastType`, `LowerConcat` — the ANSI-ish defaults are correct for most
  engines, so override only where yours differs (as SQL Server and MySQL do).
- `LowerDateAdd`, `LowerDateDiff`, `LowerDatePart`, `LowerJsonGet` — **abstract on purpose**. The
  four engines' date/JSON facilities share no portable spelling, so every dialect must supply its
  own. A dialect that cannot express a requested unit throws `SqlExprLoweringNotSupportedException`
  rather than emit a wrong approximation.

Because the switch is exhaustive over a closed hierarchy, adding a node type is a compile-time
obligation on every dialect — never a silent runtime fall-through.

## Worked module example

A module that projects an order-status label as a computed expression column. It builds the tree
once against the table and hands it to the lowering machinery — no per-dialect branching in the
module:

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

public static class OrderLabelExpression
{
    // Built once, dialect-agnostic. Renders correctly on all four engines.
    public static SqlExpr Build(IDbTable orders)
    {
        var b = SqlExprBuilder.For(orders);

        // COALESCE(UPPER(status), 'UNKNOWN') || ' #' || CAST(id AS <text>)
        return b.Coalesce(b.Col("status").Upper(), b.Lit("UNKNOWN"))
            .Concat(b.Lit(" #"), b.Col("id").Cast(SqlExprType.Text));
    }
}

// Lowering it (e.g. inside a resolver or a computed-column renderer):
var parameters = new SqlParameterCollection();
string fragment = dialect.LowerExpression(OrderLabelExpression.Build(ordersTable), ordersTable, parameters);
```

If `status` or `id` is not a real column, `SqlExprBuilder` throws `SqlExprBuildException` naming the
column the moment `Build` runs — you find out at construction, not when a query executes. This is the
same `SqlExpr` seam the structured computed-column feature (`ComputedColumnDefinition` with
`ComputedColumnKind.Expression`) is built on; see [Computed Columns &
Validation](/BifrostQL/concepts/computed-columns-and-validation/).
