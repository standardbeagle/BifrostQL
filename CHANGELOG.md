# Changelog

- Add `policy-row-scope-exempt` grants for tenant-filtered row-scope bypass.

All notable changes to BifrostQL after `3c42a60` (`[DART-xDCKBXmI5qsv] add app-builder extraction plan from Membership Manager build`).

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); pre-1.0 BifrostQL still uses CommitsSinceBaseline-style versioning.

## Unreleased — 2026-08-22

### Added — per-action grants in `policy-actions`

- `policy-actions` tokens accept one grant bracket each: `main.projects { policy-actions: read, create, update[projects.manage], delete[projects.manage,invoices.manage] }`. A bracketed action passes when the caller holds ANY listed grant; a bracketless token is unconditional. The bracket grammar is the same tokenizer the state-machine `transitions` key uses. Unknown tokens and malformed brackets fail model load naming the valid actions.

### Changed — admin bypass no longer resurrects an unlisted action (BREAKING, D7)

- `PolicyEvaluator.CanAct`'s admin bypass now covers the *grant* requirement only. When a policy lists any actions at all, an action it omits is refused to admins too — the allow-list is a product surface, and an admin silently passing `delete` on a table whose policy lists only `read,create,update` made the lockdown unauditable. Empty-actions policies (column denies only, or `policy-default: deny` with no further metadata) keep the previous admin behavior, so administrative reads on deny-default models are unchanged. Migration: where an admin must perform the action, declare it — e.g. `delete[projects.manage,invoices.manage]`; the admin bypass covers the bracket once the action is listed.

### Added — read-side column grants with masking (`read-requires`, `deny-mode`)

- The policy engine's read side gains the grant dimension and a choice of enforcement. Column-selector rules like `main.members.cost_rate { read-requires: rates.view_cost }` gate reading that column to callers holding any listed grant; a caller holding none gets the column **masked to null** in selections — one query, per-caller nulls, instead of the request being refused. The new `deny-mode: null | refuse` key (column selector or table; column wins) picks the enforcement: `refuse` keeps the existing throw, `null` masks. Defaults preserve shipped behaviour — `policy-read-deny` still refuses unless `deny-mode: null` is set; `read-requires` masks unless `deny-mode: refuse` is set. Masking is selection-only: a masked column is still refused as a filter, sort, or aggregate input (`_agg`, grouped `<table>Aggregate`), so it cannot be used as a value oracle. The mask rides `IQueryIntentExecutor`, so pgwire/OData/gRPC/MCP inherit it; mask-able columns are emitted nullable in the GraphQL type even when the column is `NOT NULL`. A typo'd `deny-mode` value fails model load.

### Added — write-side column grants (`write-requires`, `policy-write-deny-roles`)

- The policy engine's write side gains a grant dimension. Column-selector rules like `public.users.cost_rate { write-requires: team.manage }` (or `public.*.cost_rate { … }`) gate writing that column to callers holding any listed grant. The gate is presence-keyed: an update that omits the column is unaffected, and sending the currently stored value still counts as a write. It never applies to deletes. Table-level `policy-write-deny-roles` now qualifies `policy-write-deny` exactly as `policy-read-deny-roles` qualifies the read deny. Schema consequence: a column write-denied for every caller (unconditional `policy-write-deny`, no roles) leaves the table's insert/update input types, so a NOT NULL server-maintained column no longer makes the table un-insertable; a grant-conditional column stays in the shared input type but becomes optional.

### Added — per-request grant resolver (`IGrantResolver`)

- Applications may register `AddBifrostGrantResolver<T>()` (scoped) or a delegate overload to load a user's capability set from the database at each user-context assembly (a JWT lives for weeks; a permission change now takes effect when made). The resolver runs at the single user-context assembly point shared by every transport, and its grants are unioned into the owned `permissions` context key before any security module reads it. Transport shape: per request on HTTP mounts (GraphQL, binary WebSocket, MCP); once per connection at login on pgwire, RESP and LDAP (a permission change takes effect on the client's next connection); never for the Prometheus scrape identity. Fail closed: a throwing resolver empties the permission set (token roles remain) and logs a warning; a null result is treated as empty. Nothing changes when no resolver is registered.

### Added — deny-by-default authorization

- Models may set `:root { policy-default: deny }`; every table without its own policy then denies all actions and disappears from schema surfaces. The default is stamped onto tables from the model's unified metadata, so it applies whether it arrives as a rule string or through the hosting API's root metadata. The admin role still bypasses it.

### Fixed — policy grants

- Authorization policies now evaluate grants, the case-insensitive union of roles
  and permissions. Every `-roles` policy key accepts either kind of grant.

### Fixed — write path resolves schema-qualified table names (M11-w)

- `MutationIntent.Table` / `MutationBatchIntent.Table` (the `IMutationIntentExecutor` adapter write seam) and the `table` argument of `_fileUpload` / `_fileDelete` / `_fileDownload` now accept a schema-qualified `schema.name` and resolve it exactly; a bare name still resolves when it is unique across schemas. Previously every client-supplied name went through a bare-`DbName` lookup, so a table existing in two schemas (`sales.orders` + `archive.orders`) was unwritable through these surfaces — the write was refused fail-closed. An ambiguous or unknown name produces the same sanitized error (`BifrostErrorSink.LookupMiss`), byte-identical and carrying no table name. The resolution rule is the new `IDbModel.TryGetTableFromClientName`; the five keyed-write seams (`TableMutationPipeline`, `BatchMutationPipeline`, `BulkBatchPlanBuilder`, `FilteredUpdatePipeline`, `FilePointerAccess`) are unchanged — they receive the resolved `IDbTable`.

### Fixed

- Lost-update `CONFLICT` errors no longer expose the database schema-qualified table
  name. The stable message remains actionable (reload and retry), while the full
  table detail is logged server-side. Breaking for a caller that string-matched the
  old `Update of '<schema>.<table>' was rejected: …` text: branch on
  `ErrorCode == "CONFLICT"` instead; the code is unchanged.
- The `ConcurrencyMutationTransformer` pre-write rejects (missing token, and a token
  whose type is unsupported or whose numeric value cannot be advanced) no longer expose
  the database schema-qualified table name. The two conditions remain distinguishable on
  the wire by their message text — `The update must include the concurrency token column
  '<col>' …` versus `The concurrency token '<col>' …` — and carry no `ErrorCode` (as before,
  they surface as a generic fault); the model-derived `schema.table` is logged server-side
  through `BifrostErrorSink`. Breaking for a caller that string-matched the old
  `Update of '<schema>.<table>' must include …` / `Concurrency token '<col>' on
  '<schema>.<table>' …` text — match the token-column name and condition wording instead.
- Tree-sync soft-delete of a scoped-away inferred target now fails the sync instead of committing. An INFERRED delete is one the reconcile diff derived itself — an orphan `TreeSyncEngine` just read — so zero affected rows means the statement silently did nothing; it now throws and rolls the whole tree back, matching the hard-delete arm. Client-addressed operations are unaffected: an explicit save-tree `_op: delete` and a tree update of an out-of-scope row still return tolerantly, exactly as the per-row seam does. Provenance is carried by `TreeSyncOperation.Inferred`, stamped only by the engine's orphan producer, so the decision no longer depends on a `MutationType` the soft-delete rewrite has already changed.
- Tree-sync updates carrying a concurrency token now raise `CONFLICT` on zero affected rows, where the tree-sync seam previously accepted the count. This aligns it with the per-row and batch pipelines: all three now route zero-row handling through the single `MutationCommandExecutor.EnsureAffectedRows` policy and raise the same `CONFLICT` message the bulk executors already used.

- Bulk batch deletes now match the per-row predicate/SET split: audit stamps no longer enter hard-delete predicates, and client soft-delete predicates no longer get written into rows. Bulk delete plans also honor `ConflictOnNoRows`.
- Bulk batch delete predicates are rekeyed from GraphQL field names to database column names before the predicate/SET split, matching `TableMutationPipeline` and `BatchMutationPipeline`; previously a column whose GraphQL name differed from its database name dropped out of the delete predicate. (Grouping of rows by predicate shape was already correct — it is now pinned by a test, not changed.)

### Changed

- The keyed-write contract now has one home for argument/key shaping and one home for statement rendering and zero-row policy; a source scan guards every transformer-chain caller against hand-built predicates and key splits.

### Breaking — read-path filter builders take `IDbTable`, not a bare table name

- `TableFilter.FromObject`, `TableFilter.FromPrimaryKey` and the `TableFilterFactory` builders (`Equals`, `IsNull`, `IsNotNull`, `In`) now take the resolved `IDbTable` where they used to take a `string` table name, and `TableFilter` carries that table as `Table` (`TableName` remains, as a derived read-only `Table?.DbName` for SQL rendering only). The bare-name overloads are removed rather than kept as a fallback. A bare `DbName` does not identify a table: `DbModel` rejects a name that occurs in more than one schema, so a model carrying both `sales.orders` and `archive.orders` made every query, join, filter, grouped aggregate and pivot on *either* table fail. Every call site already held the `IDbTable`, so the fix is to pass it instead of `table.DbName`. Migration: replace `TableFilterFactory.Equals(table.DbName, col, value)` with `TableFilterFactory.Equals(table, col, value)` and `TableFilter.FromObject(filter, table.DbName)` with `TableFilter.FromObject(filter, table)`; where only a name is in hand, resolve it with the schema-qualified `IDbModel.GetTableFromDbName(schema, dbName)` / `TryGetTableFromDbName(schema, dbName, out …)`.

### Breaking — `TableFilter.ToSqlParameterized` removed; no public read-side render remains

- The single-fragment render that concatenated `{joins} WHERE {where}` is gone. Splicing that fragment after a hard `WHERE` produced `WHERE INNER JOIN ...` once already, and with zero production callers left it survived only as a trap for the next assembler. The engine renders filters through the internal `RenderParts` (separate `Joins` / `Where` / `Parameters`, each placed in its own clause); that seam is deliberately not public. Out-of-tree code that rendered a `TableFilter` to SQL itself has no replacement: hand the filter to the engine instead (`GqlObjectQuery.Filter` / `IQueryIntentExecutor`, or `MutationTransformResult.AdditionalFilter` on the write path). `RenderForMutation` stays public but accepts only the equality/IS NULL shapes mutation transformers produce. The same-named `GqlAggregateColumn.ToSqlParameterized` / `GroupedAggregateQuery.ToSqlParameterized` overloads are different types and are unchanged.

### Breaking — `PgLogin.Secret` replaced by a SCRAM verifier; `RespLogin.Secret` by `PasswordHash`

- The pgwire and RESP credential stores no longer hold plaintext secrets. `PgLogin` now carries `PgScramVerifier` (salt, iterations, StoredKey, ServerKey per RFC 5802 §3): the SCRAM path verifies the client proof against the StoredKey directly, and the cleartext path re-derives and constant-time compares via `PgScramVerifier.VerifyPassword`. `RespLogin` now carries `PasswordHash`, a one-way ASP.NET Core `PasswordHasher<string>` hash verified with `VerifyHashedPassword` (same contract as `LocalUserStore` and the OData Basic store). The unknown-user decoy work is unchanged in cost — a structurally identical decoy verifier on pgwire whose salt is derived from the username so it is stable across connections, a precomputed dummy hash on RESP — so the anti-enumeration timing invariant still holds. Migration: provision pgwire logins with `PgScramVerifier.Derive(password)` and RESP logins with `new PasswordHasher<string>().HashPassword(username, password)` at credential-creation time, and discard the plaintext. See `docs/src/content/docs/guides/pgwire.md` and `docs/src/content/docs/guides/resp.md`.

### Breaking — `ODataBasicCredential.Secret` replaced by `PasswordHash`

- The OData Basic-auth contract no longer carries a plaintext-equivalent shared secret. `ODataAuthenticator` used to compare `SHA256(secret)` to `SHA256(password)`, which forced every `IODataBasicCredentialStore` to hold the password itself. The record now carries `PasswordHash`, a one-way ASP.NET Core `PasswordHasher<string>` hash verified with `VerifyHashedPassword` (same contract as `LocalUserStore`); the anti-enumeration dummy work for unknown/disabled usernames is a precomputed hash verification. Migration: provision the hash at credential-creation time with `new PasswordHasher<string>().HashPassword(username, password)` and discard the plaintext. See `docs/src/content/docs/guides/odata.md`.

### Breaking — `_fileUpload.accessUrl` removed; presigned URLs are no longer stored

- File metadata no longer persists a presigned access URL. `FileMetadata.AccessUrl` and the `accessUrl` field on `FileUploadResult` are gone from the schema and from the stored column JSON, so a capability URL is no longer written into the database (and thence into history, CDC and audit copies). Rows that already carry a persisted `AccessUrl` still deserialize. Migration: mint a URL per read with `_fileDownload`; the removed field was either an expired 15-minute S3 URL or a server filesystem path.
- `_fileDownload`'s `expirationMinutes` is now clamped to a per-bucket maximum (`maxurlexpiry` / `maxPresignedUrlExpirationMinutes`, default 60). A caller value may only narrow the window; a non-positive value is rejected and `expiresAt` reports the clamped time.

### Breaking — `_join` / `_single` containers and `dynamic-joins` removed

- Every row type used to advertise `_join: <T>_join` and `_single: <T>_single` container fields whose `<table>(on: [String!])` members could never execute (the query builder only understood an `on: { column: { _eq: column } }` object and threw on the bare container). The containers, their `<T>_join`/`<T>_single` types, and the model-level `dynamic-joins` metadata switch (`MetadataKeys.Relationships.DynamicJoins`) are gone. `dynamic-joins` is now an unrecognized `:root` key, so a model that still declares it fails to load with the standard unknown-key error. Migration: delete `dynamic-joins` from `:root` metadata and use the FK-derived and `join`-declared relationship fields. The explicit `_join_<table>` query path now accepts only `_eq`/`_neq` as the `on` operator and reports every shape fault as an identifier-free `BifrostExecutionError`.
- The name producers for the removed containers are gone too: the two `_join_`-/`_single_`-prefixed field-name properties on `IDbTable` (and their `DbTable` implementations) are deleted. They had no remaining consumer after the container removal, and keeping them invited a future SDL emitter to re-wire a shape the query builder cannot execute. Breaking for out-of-tree `IDbTable` implementers only.

### Breaking — `_hardDelete` requires the `soft-delete-hard-role` opt-in

- `_hardDelete` is no longer generated on every soft-delete table. The schema emits it only on tables declaring `soft-delete-hard-role: <role>`, and the caller must hold that role; a programmatic mutation intent carrying `hard_delete` on a non-opted-in table is denied (`ACCESS_DENIED`). `retain` retention therefore requires the role declaration. Migration: add `soft-delete-hard-role` to each table that needs physical deletes. See `docs/src/content/docs/reference/changelog.md`.

### Fixed — zero-row update/upsert no longer returns the key

- A single-key `update` (and the update branch of `upsert`) whose row was scoped away by tenant/policy/soft-delete, or vanished, returned the supplied key as if it had been written. Both now answer `0` — the same not-found answer composite-key tables already gave — so a caller cannot learn a cross-tenant key from the response. Batch `TotalAffected` was already count-only.

### Added — multi-model mutation surface (generous save recipes)

- **`delta:`** collection-diff save on every table's mutation field: `{ inserted, updated, deleted }` flattens onto the batch pipeline in that order — one transaction, `batch-max-size`, `batch-duplicate-policy`, and the set-based bulk fast path unchanged. Returns the total affected count.
- **`save:`** explicit-ops graph save: every node of a nested `<t>_save` document states its own `_op` (`bifrost_save_op` enum), defaults are key-present-updates / key-absent-inserts, unlisted children are untouched (no orphan inference — that stays `sync:`'s reconcile semantic), root delete is legal, and no current-state load is needed. Executes on the TreeSync executor: per-node transformer chain, instance-scoped FK flow, one transaction.
- **`updateWhere:`** filtered set-update (`UPDATE ... SET ... WHERE`), **opt-in** via `filtered-update: enabled` table metadata with a `filtered-update-max-affected` cap (default 100, COUNT-prechecked in-transaction with rollback). The caller's `where` reuses the read-side filter grammar, ANDs into transformer row scope, and its columns clear the same column-permission guards as reads. Tables with hooks, state machines, or concurrency tokens refuse the set-based form.
- `TableFilter.CombineAnd` public combinator (converges three private copies).

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

### Fixed

- Preserve permission-only policy grants when replaying approved mutations.

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
