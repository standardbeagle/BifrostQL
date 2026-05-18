# `_agg` SQL generation: dialect-coupling survey

**Purpose:** Catalogue every code path that emits aggregate SQL for the GraphQL
`_agg(value:..., operation:...)` field. Identify which identifier-quoting,
join syntax, and pagination tokens are hardcoded for SQL Server vs delegated
through `ISqlDialect`. The TDD work in this workspace (`bifrostql`) uses this
as the gap map.

## Trigger surface

| Component | Role |
|---|---|
| `Schema/TableSchemaGenerator.cs:65` | Emits the GraphQL field `_agg(operation: AggregateOperations! value: <Table>AggregateValueType!) : Float` on every table type. |
| `QueryModel/QueryField.cs:50,142,217` | Visitor classifies `_agg` field, calls `ToAggregateSql` to build a `GqlAggregateColumn`. |
| `QueryModel/GqlObjectQuery.cs:79-84` | For every `AggregateColumn`, calls `col.ToSqlParameterized(dialect, filter)` and stores under key `"{table}=>agg_{alias}"`. |
| `QueryModel/GqlAggregateColumn.cs` | Builds the SQL string itself. **Primary emission point.** |
| `Resolvers/BifrostDispatcher.cs:122` | Wires the `_agg` field to itself as resolver. |

## Hardcoded dialect tokens (gap list)

Each row below is a place where SqlServer-flavored SQL leaks past `ISqlDialect`.
The dialect parameter is already threaded through `ToSqlParameterized` and
`AddSqlParameterized`; these sites just don't use it.

### `GqlAggregateColumn.ToSqlParameterized`

| Line | Hardcoded fragment | Why it breaks non-SqlServer |
|---|---|---|
| 34 | `firstLink.GetSqlSourceColumns(...)` returns `[col]`-bracketed text | Sqlite/Postgres expect `"col"`; MySQL expects `` `col` `` |
| 34 | `firstLink.GetSqlSourceTableRef(...)` returns `[schema].[table]` | Same — wrong quoting per dialect |
| 39 | Literal `[src]` and `[next]` table aliases | Aliases survive across dialects, but mixing bracket-quoted aliases with unquoted ones is jarring on Postgres |
| 39 | `GetSqlDestTableRef(...)` returns `[schema].[table]` | Bracket-quoted ref |
| 39 | `GetSqlDestJoinColumn(...)` returns the unquoted column name, then template wraps it in `[...]` | Bracket-quoted |
| 42 | `[src].[srcId]` + `[next].{escape}` mix | Aliases bracketed, joined col uses `dialect.EscapeIdentifier(FinalColumnName)` — partial dialect awareness |
| 43 | `{link.GetSqlSourceColumns(...)} ... [src].[srcId]` | Bracket-mixed |
| 48 | `" GROUP BY [src].[srcId]"` literal | Bracket-quoted alias |

**Note:** Only line 42 uses `dialect.EscapeIdentifier` (for the aggregated column
name itself). Everything else round-trips through `TableLinkDto` helpers that
hardcode `[...]` regardless of dialect.

### `Model/TableLinkDto.GetSqlSourceColumns` (DbModel.cs:424-443)

Hardcoded `[name]` brackets at lines 428, 430, 432, 435, 437, 440 — six sites.
This helper is the root of nearly every leak above; fixing it (and accepting
a dialect parameter) cascades through `GqlAggregateColumn`.

### `Model/DbTable.DbTableRef` (DbTable.cs:63)

```csharp
public string DbTableRef => string.IsNullOrWhiteSpace(TableSchema)
    ? $"[{DbName}]"
    : $"[{TableSchema}].[{DbName}]";
```

Computed property, no dialect access. Every caller assumes SqlServer brackets.
Either:
- Drop the property and switch callers to `dialect.TableReference(schema, name)` (already exists, dialect-aware).
- Or add an overload that takes `ISqlDialect`.

The dialect-aware path is preferred — `ISqlDialect.TableReference` is the
existing escape hatch and already implemented per-dialect.

### `Model/DbModel` link helpers (DbModel.cs:403-422)

`GetSqlSourceTableRef`, `GetSqlDestTableRef`, `GetSqlDestJoinColumn` all return
strings derived from `DbTableRef`. They inherit the bracket-hardcoding.

## Pagination / row-limiting

`GqlAggregateColumn.ToSqlParameterized` does **not** emit pagination — aggregate
queries return one row per `srcId` and don't paginate themselves. Pagination
applies to the parent table query (`GqlObjectQuery.AddSqlParameterized`), which
already delegates to `dialect.Pagination(...)`. So aggregate SQL is
pagination-clean; the only dialect coupling is identifier quoting + table refs.

## Existing dialect API

| Member | Purpose |
|---|---|
| `ISqlDialect.EscapeIdentifier(string)` | Per-dialect quoting (already used at line 42) |
| `ISqlDialect.TableReference(schema, name)` | Per-dialect qualified table ref |
| `ISqlDialect.Pagination(sortCols, offset, limit)` | Per-dialect OFFSET/LIMIT |
| `ISqlDialect.Concat` / `LastInsertedIdentity` / `ReturningIdentityClause` | Other dialect tokens (not relevant to `_agg`) |

The plumbing is in place; the refactor is mechanical.

## Test coverage today

| Test | Status |
|---|---|
| `SqlVisitorToSqlTest.JoinCountQuerySuccess` | passes (asserts SqlServer-shaped SQL string) |
| `SqlVisitorToSqlTest.DoubleJoinAvgQuerySuccess` | passes (SqlServer-shaped) |
| `SqlVisitorToSqlTest.FilteredJoinAvgQuerySuccess` | passes (SqlServer-shaped) |
| `SqlVisitorToSqlTest.SimpleCountQuerySuccess` | **skipped** — bare-column form not implemented |
| `SqlVisitorToSqlTest.SimpleAggQuerySuccess` | **skipped** — top-level `__agg_<table>` field not implemented |
| `SqlVisitorToSqlTest.SimpleAggAndJoinQuerySuccess` | **skipped** — same as above |
| **Any** integration test executing `_agg` end-to-end | **none across all four engines** |

Unit-level coverage validates SqlServer-shaped SQL strings only. Sqlite/
Postgres/MySQL agg paths have never been exercised against a real DB.

## Refactor plan implied by this audit

1. Add dialect parameter to `TableLinkDto.GetSqlSourceColumns`,
   `GetSqlSourceTableRef`, `GetSqlDestTableRef`, `GetSqlDestJoinColumn`. Route
   identifier quoting through `dialect.EscapeIdentifier`. Use
   `dialect.TableReference` for qualified refs.
2. Replace `DbTable.DbTableRef` callers (in agg-emission paths) with
   `dialect.TableReference(table.TableSchema, table.DbName)`. Leave the
   property in place for non-agg callers until they migrate.
3. Update `GqlAggregateColumn.ToSqlParameterized` to use the dialect-aware
   helpers above and to quote `[src]` / `[next]` aliases via
   `dialect.EscapeIdentifier`.
4. Update the SqlVisitor unit-test fixtures (`SqlVisitorToSqlTest`) to assert
   dialect-rendered strings — since the existing fixture is SqlServer-flavored
   and other dialects produce the same string when running through
   `SqlServerDialect`, the unit tests should still pass without modification
   when the dialect is SqlServer. Cross-dialect unit assertions need new test
   cases.
5. Add **integration** tests per engine that run `_agg(value: { joinTable: col }
   operation: count|sum|avg)` against the real container and assert numeric
   results. These tasks already exist in worktrack workspace `bifrostql`.

## Out of scope for this audit

- Bare-column single-table `_agg(value: <col>)` form (separate task).
- Top-level `__agg_<table>(...)` field (separate task).
- Pivot SQL generation (`PivotSqlGenerator.cs`) — different feature, also
  SqlServer-flavored, not covered here.
- Paged joins on linked tables (separate task).
