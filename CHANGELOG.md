# Changelog

All notable changes to BifrostQL after `3c42a60` (`[DART-xDCKBXmI5qsv] add app-builder extraction plan from Membership Manager build`).

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); pre-1.0 BifrostQL still uses CommitsSinceBaseline-style versioning.

## Unreleased

### Added — state-machine + workflow

- New `Auth/StateMachineConfigCollector`, `StateMachineDefinition`, `StateTransitionInfo`, and `StateTransitionAuditObserver` wire a metadata-driven state machine into the mutation pipeline.
- `Modules/StateMachineMutationTransformer` enforces role-qualified transitions; `MutationObservers` and `StateTransitionObservers` fire post-commit and are fail-soft (per-observer try/catch + `ILogger`).
- `Workflows/` runtime: `IWorkflowRunner`, `IWorkflowDataExecutor`, `WorkflowDefinition`, `WorkflowScheduler`, `WorkflowTriggerHost`, `WorkflowConfigCollector`. Membership-manager sample wires it via `HostedSpa`.
- `DbTableBatchResolver` collects a `BatchActionOutcome` per action and notifies the observer chain after the batch transaction commits — previously batch insert/update/delete/upsert produced no notifications.

### Added — dialect support

- `ISqlDialect.SupportsNativePivot` (default `false`, `true` on SqlServer). `PivotSqlGenerator.GeneratePivot` is the dialect-aware entry point that routes to SqlServer native PIVOT or the engine-agnostic CASE WHEN cross-tab.
- `ISchemaReader.SchemaData` carries an `IReadOnlyList<DbForeignKey> ForeignKeys`; all four schema readers (`Sqlite`, `SqlServer`, `Postgres`, `MySql`, plus the legacy Core copy of `SqlServerSchemaReader`) now load FK metadata from the catalog. Enables self-FK relationship detection across all engines.
- `_agg(value: { joinTable: { column: ... } } operation: ...)` SQL emission flows through `ISqlDialect.EscapeIdentifier` + `TableReference` (was bracketed for SqlServer only). Verified end-to-end on SqlServer, Sqlite, Postgres, MySQL for Count/Sum/Avg/Min/Max.

### Added — tests

- Integration tests against real `mcr.microsoft.com/mssql/server:2022-latest`, `postgres:16`, and `mysql:8.4` containers via the `.NET` workflow service definitions. Sqlite runs in-memory.
- `Aggregate_NestedJoin{Count,Sum,Avg,Min,Max}_ShouldReturn*` in each `*FullIntegrationTests` suite.
- `SelfReferencingTable_ShouldHaveSelfJoin` un-skipped for all four `*SchemaLoadingTests` (was deferred — the FK-loader gap is closed).
- `BareColumnAggregate_ThrowsWithClearError` codifies the contract for `_agg` without a nested-FK link.
- `PageBaseQuerySuccess` un-skipped: linked sub-queries now forward parent `Offset`/`Limit`/`Sort` when bounds are positive.
- `FakeSchemaLoadsWith{,out}DynamicJoins` un-skipped after aligning `GetFakeTables` fixture columns with the schema generator (every column now carries `GraphQlName`; non-identifier characters sanitized to `_`).

### Changed — production

- `PostgresDialect.ReturningIdentityClause` is now `null`; the resolver falls back to `SELECT lastval() ID`. The hardcoded `RETURNING id AS ID` clause assumed an `id` column on every table, which broke the common `<table>_id` convention.
- `SqlServerDialect.ReturningIdentityClause` is now `null`; the resolver falls back to `SELECT SCOPE_IDENTITY() ID`. The previous `OUTPUT INSERTED.id AS ID` clause sat in the wrong syntactic position (after `VALUES` rather than between the column list and `VALUES`).
- `TableLinkDto.GetSqlSource{Columns,TableRef}` / `GetSqlDestTableRef` take an `ISqlDialect` and route quoting through the dialect.
- `GqlObjectQuery.GetRestrictedSqlParameterized` forwards parent pagination into the linked DISTINCT sub-query when `Offset > 0` or `Limit > 0` (skips the `Limit == -1` "no limit" sentinel and `Offset == 0` to avoid `42P10` and MySQL "DISTINCT incompatible with ORDER BY").
- `GqlObjectQuery` sort-token switches throw `BifrostExecutionError` with the offending token and supported suffixes instead of bare `NotSupportedException()`.
- Deleted unimplemented `SimpleAggQuerySuccess`, `SimpleAggAndJoinQuerySuccess`, and `SimpleCountQuerySuccess` tests; the top-level `__agg_<table>` field they exercised was never wired into production.

### Fixed

- Sqlite/Postgres/MySql schema-loading tests use per-test-instance DB names so xunit fixtures don't collide on the shared cache.
- `SqlServerSchemaLoadingTests` GO-batch split uses a multi-line, case-insensitive regex on lines containing just `GO` (the previous substring split fired inside identifiers like `Orders`).
- `ListDatabasesTests` Postgres finalizer splits `pg_terminate_backend` + `DROP DATABASE` into two separate `ExecuteNonQuery` round-trips (Npgsql 9 implicitly pipelines multi-statement CommandText, and `DROP DATABASE` is rejected inside a pipeline with `25001`).
- `BifrostUITemplateTests` resolves `Program.cs` via `[CallerFilePath]` instead of a hardcoded absolute path that broke outside one developer's checkout.
- `ParseWpConfigOutput` reframes the "expected JSON array" branch to call out wrong-format JSON (was misleadingly labelled "non-JSON output").

### CI

- `.github/workflows/dotnet.yml`: bumped `actions/checkout` v3 → v4 and `actions/setup-dotnet` v3 → v4. `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true` set on all three workflows ahead of the 2026-09-16 Node.js 20 runner removal.
- `SqlServer 2022`, `Postgres 16`, and `MySQL 8.4` services attached to the build job. `Test Integration` step exercises Sqlite (always), SqlServer, Postgres, MySQL via `BIFROST_TEST_*` env vars. MySQL `max_connections` raised to 500 to survive parallel xunit fixtures.
- Dependabot alerts: 0 open (was 20). `pnpm audit` clean. vitest 2 → 3, vite 5 → 7, `@vitejs/plugin-react-swc` 3 → 4 in workspace packages.

### Docs

- `docs/research/agg-dialect-survey.md` enumerates every `_agg` SQL emission path, flags hardcoded dialect tokens, and pins the gap list the cross-dialect work closed.
- `SKILL.md` placeholder for agent skill discovery.
- `docs/src/content/docs/guides/state-machines.md` and `docs/src/content/docs/guides/workflows.md` cover the new subsystems.

### Known gaps (tracked in worktrack workspace `bifrostql`)

- Pivot end-to-end coverage across the four engines (`PivotSqlGenerator` is not yet wired into the GraphQL execution pipeline).
- Composite-key foreign-key relationships still hard-skipped in all three relationship strategies.
- E2E coverage for state-machine + workflow + AppMetadata subsystems is unit-level only.
- `BifrostDispatcher` per-pair `_join_<table>` / `_single_<table>` wiring is inconsistent with the schema generator output (silently harmless today).
- `BifrostQL.Host` has no integration smoke test.
- `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` override removable once Actions runner default flips to Node.js 24 (or upstream actions publish v5).
- Two separate WorkTrack stores (REST container vs local MCP) for the planning workspace — drifted multiple times, needs consolidation.
