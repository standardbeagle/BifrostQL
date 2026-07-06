# Changelog

All notable changes to BifrostQL after `3c42a60` (`[DART-xDCKBXmI5qsv] add app-builder extraction plan from Membership Manager build`).

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); pre-1.0 BifrostQL still uses CommitsSinceBaseline-style versioning.

## 0.4.11 — 2026-06-23

### Fixed — polymorphic child with no scalar columns crashed SQL generation

- A polymorphic (or child) collection selected with only the relationship — no scalar fields of its own — produced a malformed connected projection `SELECT [a].[src_id], FROM (...)` → `Incorrect syntax near ','`. The builder hardcoded `"{src}, {childColumns}"` and `childColumns` was empty. Now the child projection is appended only when non-empty.
- Regression: `PolymorphicJoinSqlTests.CompaniesNotes_NoChildColumns_EmitsValidProjection`.

### Added — generated-SQL parse validation in tests

- New `SqlSyntax` test helper validates generated SQL against each engine's real grammar: Microsoft ScriptDom (TSql160) for SQL Server, `SqlParserCS` (new test-only dependency) for Postgres, MySQL, and SQLite.
- `GeneratedSqlValidityTests` parses the actual `AddSqlParameterized` output (single-table, select+join, paged child) across all four dialects, so structural defects (stray commas, empty projections) fail fast instead of surfacing only against a live database.

## 0.4.10 — 2026-06-22

### Fixed — `time`/`date` columns crashed the String scalar

- SQL `time`/`date` columns map to the GraphQL `String` scalar, but ADO providers hand back `TimeSpan`/`TimeOnly`/`DateOnly` (SQL Server `time` → `TimeSpan`; Npgsql `time`/`date` → `TimeOnly`/`DateOnly`). GraphQL.NET's `StringGraphType.Serialize` throws on non-string values, so these columns failed serialization.
- Fix normalizes them to round-trippable ISO strings in `ReaderEnum.DbConvert` — the single choke point for every read path (top-level, sub-table, single-row). `DateTime`/`DateTimeOffset` are untouched (their own scalars handle them).
- Regression: `ReaderEnumDbConvertTests`.

### Changed — conservative many-to-many auto-detection

- Auto-detection no longer treats a two-FK table as a pure junction when it carries **real extra (non-key, non-FK) columns** — such a table is a first-class entity (e.g. `sessions → session_entries → participants`), not a link table.
- Auto-detection now also requires the junction name to reference **both** endpoint tables (the conventional `tableA_tableB` link-table naming, singular/plural tolerant).
- Either guard is overridden by explicit `many-to-many:` metadata, which still supports payload junctions.
- Regression: `ManyToManyLinkTests`, `MetaSchemaResolverManyToManyTests`.

### Added — uniform metadata activate/deactivate convention

- New `MetadataSwitch` vocabulary shared by every boolean toggle: on (`true/on/yes/enabled/1/active`) and off (`false/off/no/disabled/0/!`). Blank/unrecognized falls back to each switch's default. Routed through all `GetMetadataBool` readers plus the `raw-sql` / `enable-generic-table` checks, so `auto-join`, `foreign-joins`, `dynamic-joins`, `de-pluralize`, etc. all deactivate consistently.
- Many-to-many metadata accepts an inline `!` negation to prune a single auto-detected bridge while keeping the wide auto-detection net (e.g. `many-to-many: Groups:Memberships, !Roles`). A negation on either endpoint suppresses the whole pair.
- Regression: `MetadataSwitchTests`, `ManyToManyLinkTests`.

### edit-db (`0.3.87`)

- **Stacking-mode toggle**: a graphical switch beside the Columns selector toggles parent/child drill-down ("Stacked") vs a flat standard grid. Off collapses any open drill columns and renders FK/multi-join cells as plain values.
- **Pagination fix**: switching tables no longer strands the grid on an out-of-range page ("page 2 of 1"). The page index is clamped into range (`clampPageIndex`), which also repairs the empty-window fetch and the stuck pager.
- **JSON data support**: native `json`/`jsonb` columns (and `paramType: JSON`) now route to the content viewer with pretty-print + format/minify; object values from the GraphQL JSON scalar are serialized instead of rendering as `[object Object]`.
- **Create-flow routing fix**: opening the New-record dialog (`/:table/edit`) no longer fires a bogus get-by-id with `$id="edit"`. The router matched `/:table/:id` and captured the `edit` keyword as an id because `Routes` rendered every match and lacked a bare create route. `Routes` now renders the single most-specific match (literal segment beats `:param`), and the DataPanel block gained the missing `/:table/edit` route. Regression: `usePath.test.ts`.
- **Drill-stack scroll & collapse**: the multi-generational drill stack now shares one outer scrollbar with a per-table min-height instead of squishing. Ancestor generations auto-collapse to their selected row (the row drilled into the next level); the deepest level stays full and is badged "active". Any ancestor re-expands from its header chevron.

## 0.4.9 — 2026-06-19

### Fixed — same-table-via-two-paths join nulling

- A table reached through two different join paths in one query nulled the deeper path. Example: `board → client → users` (the lead) and `board → deliverable → users` (the owner) — both single-links carry the join field name `users`. The result reader resolved nested join fields against the root query's flattened `RecurseJoins` matched by name only, so the second path read the wrong (or empty) result set.
- Fix threads each level's `GqlObjectQuery` into `SubTableEnumerable`/`SingleRowLookup` and scopes `GetJoin`/`GetAggregate` to that level's direct `Joins`. Also repairs nested aggregate resolution, which shared the root-scoping flaw. The bug was in post-SQL result assembly, so it was dialect independent.
- Regression: `SqliteDualPathSameTableTests`.

## 0.4.8 — 2026-06-18

### Security — dependency bumps (Dependabot)

- Resolved 7 of 8 Dependabot advisories in the JS workspace: `astro` → 6.4.8 (2× high/moderate), `vite` → 7.3.5 (high/moderate), `form-data` → 4.0.6 (high), `js-yaml` → 4.2.0 (moderate, via pnpm override), `@babel/core` → 7.29.7 (low). `form-data`/`js-yaml` pinned through root `pnpm.overrides`.
- Deferred: `esbuild` (low, build-time only) stays at 0.27.7 — pinned transitively by `vite@7.3.5`; will clear when Vite bumps its esbuild range. Forcing 0.28.1 across vite/storybook/webpack risks the build for a low-severity dev-only advisory.
- All workspace builds (docs, edit-db) and JS test suites (1389 tests) pass on the updated tree.

## 0.4.7 — 2026-06-18

### Changed — edit-db navigator

- Nested records now edit **in place**. The edit dialog is prop-driven (`DataEditDialog`) and opened from each grid's local state instead of routing, so editing a child / grandchild / side-column row no longer rewrites the root route and collapses the drill context. Saved changes refetch in the grid you edited.
- Row action toolbar (edit/delete) now **overlays the row** — pinned to the row's right edge, vertically centered — instead of floating below it, so it's easy to reach. Dismisses on outside tap/click.
- Touch: the row action overlay opens on a **long-press (hold)** rather than hover; a finger move (scroll) cancels, and the follow-up click is suppressed so it doesn't also select the row.
- Many-to-many panel always shows the junction payload fields (removed the show/hide toggle).

### Changed — shared cell formatting

- All grid/detail/popover cells render through one `formatColumnValue` path. The m2m panel and the FK preview popover previously used their own renderers and missed locale-aware formatting; they now match the main grid.
- Added a secondary, context-specific format template: `display-format-preview` column metadata, which falls back to the main `display-format` when unset (used by FK preview popovers). Removed the now-dead `renderScalarValue` and the popover's private `formatValue`.

### Docs

- New guides: Extending BifrostQL (hooks & providers), React Hooks & Components, Embeddable Data Editor; new concept: EAV & the `_meta` field.
- Sidebar now surfaces the previously-orphaned concept pages (computed columns, lookup-table enums, pivot, visual query builder) and a Desktop Navigator section.
- Documented the new API surface: generic `Add*Transformer<T>` registration + metadata auto-registration, before-commit veto hooks, async `TransformAsync`/`ValidateAsync`, soft-delete `_onlyDeleted`/`_hardDelete` args, `_availableTransitions`, and `_agg` aggregates.

## 0.4.1 — 2026-05-18

### Added — composite foreign keys

- `TableLinkDto` carries `ChildIds`/`ParentIds` ordered column lists alongside the back-compat scalar `ChildId`/`ParentId`; `IsComposite` shortcut on both `TableLinkDto` and `TableJoin`.
- `ForeignKeyRelationshipStrategy` now resolves and links composite FKs across all four dialects (previously skipped).
- SQL emitter routes single-column joins through the historical `JoinId` / `src_id` aliases and composite joins through suffixed `JoinId_<i>` / `src_id_<i>` aliases. Multi-column ON-clauses AND every per-column equality.
- `ReaderEnum` resolves composite join keys via `JoinKeyValues.FromParentRow` + `JoinKeyMatcher.FilterRows`/`FindRow`. Both `Single` and `Join` query types pass through one path.
- New `JoinKeyNames` is the single source of truth for the join-alias convention.
- `TableJoin` owns its own SQL emission (`EmitJoinIdProjection`, `EmitSrcProjection`, `EmitOnClause`); `GqlObjectQuery` shrinks ~60 LOC and no longer knows the alias scheme.

### Added — local dev infra

- `docker-compose.test.yml` stands up sqlserver 2022 + postgres 16 + mysql 8.4 with the same images, ports, and creds as `.github/workflows/dotnet.yml`.
- `scripts/test-env.sh` exports `BIFROST_TEST_{SQLSERVER,POSTGRES,MYSQL}` matching CI byte-for-byte.

### Added — tests

- 8 new composite-FK integration tests (2 per dialect × sqlite/sqlserver/postgres/mysql) exercising child→parent and parent→children navigation against a `TenantInventory`→`TenantLocations` fixture with a colliding `LocationId=10` across tenants. A single-column join would crosswire those rows; the composite emission keeps them isolated.
- New `CompositeJoin_EmitsAndedOnClause_WithSuffixedJoinIds` Core unit test asserts the suffixed-alias SQL shape.
- `ForeignKey_Composite_IsSkipped` flipped to `ForeignKey_Composite_LinksWithFullColumnLists`.

## Unreleased

### Added — state-machine + workflow

- New `Auth/StateMachineConfigCollector`, `StateMachineDefinition`, `StateTransitionInfo`, and `StateTransitionAuditObserver` wire a metadata-driven state machine into the mutation pipeline.
- `Modules/StateMachineMutationTransformer` enforces role-qualified transitions; `MutationObservers` and `StateTransitionObservers` fire post-commit and are fail-soft (per-observer try/catch + `ILogger`).
- `Workflows/` runtime: `IWorkflowRunner`, `IWorkflowDataExecutor`, `WorkflowDefinition`, `WorkflowScheduler`, `WorkflowTriggerHost`, `WorkflowConfigCollector`. Membership-manager sample wires it via `HostedSpa`.
- `DbTableBatchResolver` collects a `BatchActionOutcome` per action and notifies the observer chain after the batch transaction commits — previously batch insert/update/delete/upsert produced no notifications.

### Added — lookup-table enums

- Lookup-table enums: tables marked `enum:` emit GraphQL enum types (`{Table}Values`); FK / `enum-ref` columns are typed, filterable, and writable as enums with value↔name mapping across all four engines (value-valued / Approach A). Soft-deleted lookup rows are excluded from membership; membership is per-connection (not tenant-scoped); the redundant FK navigation field is suppressed when its column is an enum. Drift reads as `null` with a logged warning. See `concepts/lookup-table-enums`.

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
- Composite-key foreign keys are resolved, linked, and emitted by `ForeignKeyRelationshipStrategy` since 0.4.1, but the heuristic detection strategies (name-based, many-to-many, polymorphic) still skip composite keys.
- E2E coverage for state-machine + workflow + AppMetadata subsystems is unit-level only.
- `BifrostDispatcher` per-pair `_join_<table>` / `_single_<table>` wiring is inconsistent with the schema generator output (silently harmless today).
- `BifrostQL.Host` has no integration smoke test.
- `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` override removable once Actions runner default flips to Node.js 24 (or upstream actions publish v5).
- Two separate WorkTrack stores (REST container vs local MCP) for the planning workspace — drifted multiple times, needs consolidation.
