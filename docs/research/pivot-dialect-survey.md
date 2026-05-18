# Pivot SQL: dialect-coupling survey

**Purpose:** Catalogue every code path in the Pivot subsystem that emits engine-specific SQL. Counterpart to `agg-dialect-survey.md`. Drives the worktrack tasks tracking pivot-related cross-dialect work.

## Trigger surface

| Component | Role |
|---|---|
| `QueryModel/PivotQueryConfig.cs` | Pure DTO. Captures `PivotColumn`, `ValueColumn`, `AggregateFunction` (count/sum/avg/min/max), `GroupByColumns`, `NullLabel`. Validated by `PivotQueryConfig.Create(...)`. No SQL. |
| `QueryModel/PivotSqlGenerator.cs` | Static helpers that build the parameterized SQL. **Sole emission point.** |
| GraphQL execution pipeline | **Not yet wired.** Pivot exists as a standalone helper; no `_pivot(...)` GraphQL field is generated and no resolver consumes `PivotSqlGenerator` output. |

## Generation modes

Two SQL shapes, plus an empty-pivot fallback and a distinct-values helper:

| Method | When |
|---|---|
| `GenerateSqlServerPivot` | SqlServer-only — emits native `PIVOT (...)` operator over a coalesced source subquery. |
| `GenerateCaseWhenPivot` | Engine-agnostic cross-tab — emits one `<agg>(CASE WHEN ... THEN ... END)` per distinct pivot value. |
| `GenerateEmptyPivot` | No distinct pivot values — returns just the group-by projection. |
| `GenerateDistinctValuesSql` | Helper to fetch the distinct pivot values (driver of the two-pass pivot flow). |

`GeneratePivot(dialect, ...)` (added by the dialect-routing task) is the public entry point that dispatches by `ISqlDialect.SupportsNativePivot`.

## Hardcoded dialect tokens (per method)

### `GenerateSqlServerPivot` (SqlServer-only)

| Line | Fragment | Notes |
|---|---|---|
| 41 | `$"ISNULL(CAST({pivotCol} AS NVARCHAR(MAX)), '{config.NullLabel}')"` | `ISNULL` and `NVARCHAR(MAX)` are SqlServer-specific. Postgres would need `COALESCE` + `TEXT`; MySQL would need `IFNULL` + `LONGTEXT`. Acceptable because the method is gated to SqlServer. |
| 54 | `PIVOT (...)` | SqlServer-native operator. |
| All | Identifiers routed via `dialect.EscapeIdentifier` (correct — even though only SqlServer hits this path, the helper stays consistent). |

### `GenerateCaseWhenPivot` (engine-agnostic)

| Line | Fragment | Notes |
|---|---|---|
| 100-105 | `$"{aggFunc}(CASE WHEN {pivotCol} IS NULL THEN {valueCol} END)"` / `... = {paramName}"` | Standard SQL. Compatible with all four engines. |
| 113-119 | Plain `SELECT … FROM … GROUP BY …` | Standard. No dialect coupling. |
| All | Identifiers routed via `dialect.EscapeIdentifier`. |

### `GenerateEmptyPivot` (engine-agnostic)

| Line | Fragment | Notes |
|---|---|---|
| 134-140 | Plain `SELECT … FROM … GROUP BY …` | Standard. |

### `GenerateDistinctValuesSql` (engine-agnostic)

| Line | Fragment | Notes |
|---|---|---|
| 159-164 | `SELECT DISTINCT … FROM … ORDER BY …` | Standard. Compatible with all four engines. |

## Dispatch entry point

`ISqlDialect.SupportsNativePivot` (defaults `false`, override `true` on `SqlServerDialect`) drives `PivotSqlGenerator.GeneratePivot`:

```csharp
dialect.SupportsNativePivot
    ? GenerateSqlServerPivot(...)
    : GenerateCaseWhenPivot(...)
```

Verified in `PivotQueryTests.GeneratePivot_On{SqlServer,Sqlite,Postgres,MySql}_*`.

## Test coverage today

| Test | Status |
|---|---|
| `PivotQueryConfig.Create` validation (function names, null handling, etc.) | passes |
| `GenerateSqlServerPivot_*` (5 cases) | passes (asserts SqlServer-shaped SQL string) |
| `GenerateCaseWhenPivot_*` (5 cases) | passes (asserts engine-agnostic SQL string) |
| `GeneratePivot_On{Dialect}_RoutesTo*` (4 cases) | passes (verifies dialect-aware dispatch) |
| `SupportsNativePivot_OnlySqlServerReturnsTrue` | passes |
| **Any** integration test executing a pivot query end-to-end | **none across all four engines** |

The unit-level coverage is comprehensive for the SQL shape, but no integration test runs a pivot query against a real DB. The worktrack task **"Add pivot e2e integration tests (count/sum/avg per engine)"** captures that work, but it cannot proceed without first wiring `PivotSqlGenerator` into the GraphQL execution pipeline.

## Open gaps

1. **No GraphQL surface.** Pivot is a standalone helper; nothing in the schema generator emits a `_pivot(...)` field, and nothing in `SqlVisitor`/`GqlObjectQuery` plumbs the helper's SQL into the execution flow.
2. **No e2e tests.** Direct call sites for `PivotSqlGenerator.*` are unit tests only.
3. **Wide-but-shallow CASE WHEN cost.** The fallback emits one CASE per distinct pivot value — performance degrades linearly with pivot cardinality. Acceptable for catalogue-style pivot but a known cliff for high-cardinality columns.
4. **NULL-label leak.** The SqlServer path inlines `config.NullLabel` directly into the SQL string (line 41). Acceptable when `NullLabel` is validated by `PivotQueryConfig.Create`, but worth a sanitization pass before wiring to user-controllable input.

## Refactor plan implied by this audit

1. Wire pivot into the GraphQL pipeline (out of scope for this survey; worktrack task: "Add pivot e2e integration tests …" depends on it).
2. Once wired, mirror the `_agg` per-engine integration tests (count/sum/avg/min/max) against the four FullIntegration containers.
3. Consider promoting `GeneratePivot` to the primary public API and marking the per-shape methods `internal` to discourage dialect-specific call sites.
