---
title: "Changelog"
description: "User-facing behavior changes, including breaking changes and how to migrate."
---

Notable user-facing changes to BifrostQL. Breaking changes list the migration step. The full
list, including every fix, is in
[CHANGELOG.md](https://github.com/standardbeagle/BifrostQL/blob/main/CHANGELOG.md) on GitHub.

Each section is headed by the git tag that shipped it and names its NuGet build. NuGet packages
are versioned `0.4.<CI run number>`, so a NuGet number does not match its tag: `v0.4.28` is NuGet
`0.4.740`. When a tag's publish failed, its changes first reached NuGet with the next tag, and the
section is headed by that tag. The package list is in the
[README](https://github.com/standardbeagle/BifrostQL#packages), and the full tag-to-build table
is under "Versions" in CHANGELOG.md.

## Upgrading from an older build

Read every section from the one after your build up to the build you are moving to, and apply each
migration step on the way. A host on NuGet `0.4.709` (`v0.4.22`) reads every section from 0.4.23 up
to Unreleased. Releases with no section here (0.4.23, 0.4.26, 0.4.27) made no breaking change;
their fixes are in the full changelog.

## Unreleased

On `main` after v0.4.30; nothing yet.

## 0.4.30 — 2026-11-22 (link keys and operation-id reads)

Tag `v0.4.30` · NuGet `0.4.831` · npm `@bifrostql/react` and `@bifrostql/types` unchanged at `0.4.1`

### Fixed — link keys and operation-id reads

- **Links over a `uuid` key resolve on PostgreSQL.** A to-one link read null, and to-many and many-to-many links read empty with `total: 0`, over every key PostgreSQL reads as text: `uuid`, date and timestamp types, `bytea`, `money`, `inet` and enums. Row aggregates over such a key did not reach their rows either. A link's key is now read the way its parent row reads it.
- **A link over a composite key resolves when the query selects none of its key columns.** Every row failed with "join column not found."
- **A request repeating an operation id that another request is writing reads the record once.** It read it before and after waiting on the first request. A write now claims its id before it reads, so a new write reads nothing. A repeat of an already-committed id costs a refused claim plus one read, and on PostgreSQL the refused claim logs a unique-violation error.

### Security — fifth review pass

- **`bifrostui` on its loopback bind refuses requests whose Host header is not `localhost`, `127.0.0.1` or `[::1]`.** The desktop host had no Host filtering, so a DNS-rebinding page became same-origin and could reach `/api/sqlite/connect`, `/api/vault/*`, `/graphql` (auth disabled) and `/_bridge`. `--expose` keeps accepting any host.
- **Every `/_bridge` POST has its Content-Type checked and its body parsed, including chunked requests with no Content-Length.** Both checks were gated on `Content-Length > 0`, so a chunked POST skipped them and ran the handler with an empty payload.
- **pgwire `MaxSessionPortalBytes` also counts Bind parameter bytes and the SQL text each portal pins.** Bind values and the statements portals kept alive after a re-Parse were never charged, so `Bind p1..p200` with 1 MiB values and no Sync held hundreds of MiB per session. Close('P') and Sync refund them.
- **`type: url` columns in the generated detail view are links only for absolute http/https URLs.** HTML encoding left `javascript:` and `data:` schemes intact, so a stored value could run script when clicked. Other values render as plain text.
- **`SecretScrubber` masks single-quoted and doubled-quote-escaped `Password`/`Pwd`/`User Id`/`Uid` values.** `Password='ab;cd'` used to leak `cd'`.
- **A history row's `entity_id` stores key values as their column type.** A write addressed by `_primaryKey: ["5"]` recorded `{"id":"5"}` while the insert recorded `{"id":5}`, so a right-to-erasure purge by the typed key left the string-keyed rows, and their before-images, behind. Rows already written with string keys are not migrated.
- **A frontend that forwards client context can no longer set the model's `user-audit-key` and author a write.** The tenant key was identity-owned; the configured actor key was not, so a caller whose identity lacked it could name any author.
- **Wire context can no longer set `tenant_id` when `tenant-context-key` names another key, or suppress the state-transition audit row with a key that differs only in case (`User`).** The case twin made the audit observer's copy throw; the error was swallowed, so the transition committed unaudited.
- **CLI commands never print a provider's connection-string parse error.** A password containing an unquoted `;` was quoted back (`Keyword not supported: 'cd'`) by `config-validate`, `test`, `doctor` and `watch` (every poll). An unhandled CLI exception that is or wraps an `ArgumentException` or `DbException` is reported by type only; others print their message. Postgres/MySQL failures in `schema`, `export` and `config-generate` no longer crash.
- **A raw-claim tenant (`tenant-context-key` fed by a raw principal claim) is read as its single value everywhere.** Deferred change sets, pending changes, approvals and outbox rows recorded it as `"System.String[]"`, which merged every tenant: one org's review queue listed another's change sets, undo crossed tenants, and `pending_changes` reads returned other tenants' rows. A multi-valued tenant claim is refused, including by operations that touch no tenant table. Rows already stamped `"System.String[]"` match no caller and their approvals are refused; re-stamp them from `requester_context` or reject them.
- **`transition-not-by` compares actors as values.** A numeric actor claim (`42.0` against a stored `42`) or a non-invariant culture made the author look like a different actor, so the author could approve their own row. JSON-number actors that are not integers are refused.
- **`transition-not-by` no longer lets an author self-approve when the actor is a large number written in exponent form** (`1E+17` against `100000000000000000`).
- **A host `IBifrostAuthContextFactory` context is normalised to case-insensitive keys at every identity seam, so reads and writes agree; keys that differ only in case are refused.** Reads scoped by `Org_Id` while writes answered "Tenant context required", and on MCP twin keys crashed session setup instead of refusing.
- **`transition-not-by` compares exponent-form actors as doubles,** so a non-round large double (`1.2345678901234568E+17`) no longer lets its author self-approve.

### Breaking — fifth review pass

- **OData `$top` caps the total rows across all pages, not the page size.** `$top=10` returned 10 rows plus a nextLink that re-sent `$top`, so a client following nextLink walked the whole entity set. The end of the walk now travels in the signed `$skiptoken` (version `odatact2`); tokens minted before this change are refused with a 400. `ODataContinuationToken.Decode` returns `ODataPagePosition`.
- **Root fields bind to their own parsed node, and only the executing operation is parsed.** An unaliased field could pick up an aliased sibling's node (`{ a: users(filter:…) {…} users {…} }` returned only `a`'s rows), and with several operations in one document another operation's node could answer. `IBifrostFieldContext` adds `Operation`; `new SqlVisitor()` throws on a multi-operation document.
- **`_table` is bounded by `max-query-rows` as well as `generic-table-max-rows`, and `limit: 0` returns no rows.** It used to return up to `generic-table-max-rows` whatever the ceiling, and `limit: 0` meant the maximum.
- **A `key-ref` whose id ends in `v` and a whole number of 2 or more (for example `config:piiv2`) is refused at model load and by `EnvelopeKeyManager`.** Version N of `config:pii` is stored at `config:piivN`, so the two key-refs silently shared a data key. A deployment already using such a key-ref must re-encrypt under a new name.
- **Malformed `prefix-groups` entries, and prefixes that match no table, fail model load.** They were skipped silently.
- **`soft-delete-by` records the `user-audit-key` actor, the same value audit and history record.** It read a hardcoded `user_id` claim, so with `user-audit-key: email` the three disagreed. A host relying on the old claim adds `:root { user-audit-key: user_id }`; without it, model load fails (see below). `SoftDeleteMutationTransformer.UserIdContextKey` is removed.
- **State transitions and workflow triggers identify the row by its full key and the table by `schema.name`.** Transitions carried only the first key column, so composite-key rows were indistinguishable, and triggers matched the bare table name, so a workflow for `employee` fired for every schema. Triggers accept `schema.name`; a bare name matches only when it is unique. Trigger inputs replace `entityId` with `entityKey`, `entity` and `table` are schema-qualified, and the transition `audit_log` row's `entity_id` is the key as JSON. `StateTransitionInfo` carries the resolved table and `EntityKey`.
- **Model load fails when `soft-delete-by` or `populate: created-by`/`updated-by`/`deleted-by` is declared without a model-level `user-audit-key`.** Those columns were silently never stamped. The legacy `audit-user-key` was accepted but never read; it is now refused (see below). The shipped Host `appsettings.json` used it and is fixed.
- **`<table>Pivot` returns at most `max-query-rows - 1` rows; the last row fetched is the truncation check, as on every other capped surface.** It fetched one row past the ceiling, and a ceiling of `int.MaxValue` overflowed into a negative FETCH/LIMIT.
- **Model load refuses the retired `audit-user-key` and names its replacement, `user-audit-key`.** It was accepted and never read, so a model setting only it recorded a null actor in audit columns, history and state transitions.
- **`bifrost config-validate` parses with the server's metadata loader and, given `--connection-string`, runs model-load validation.** Its own parser disagreed with the runtime (comma selectors, `;` inside backticks, duplicate keys, malformed segments, `:auth`) and never ran model-load checks. The connection string is now optional. `ConfigValidator`, `ConfigValidationIssue` and `ConfigIssueSeverity` are removed with no public replacement; use `new MetadataLoader(rules)` and `DbModelLoader.LoadAsync`.
- **A write whose `user-audit-key` claim holds more than one value is refused.** Raw principal claims such as `sub` are copied as string arrays, so the actor bound an array or recorded `"System.String[]"`. Use `user_id`; the docs used `sub` and are corrected.
- **A metadata rule whose selector starts with `:` and is not `:root` (for example `:auth`) is refused at model load.** Such rules matched nothing and were dropped silently, typos included. The shipped Host `appsettings.json` loses its inert `:auth { enable-auth: false; }` rules.
- **`MetadataValidator.ValidateTableMetadata`, `ValidateColumnMetadata` and `ValidateDatabaseMetadata` are removed.** Nothing called them but their tests, and they had drifted from the load-time gate. Nothing public replaces them; `ModelConfigValidator` runs at model load.
- **A raw `tenant_id` token claim no longer supplies the tenant when the identity has none.** It filled the key the OIDC mapper had chosen not to project. Project the tenant through an `IOidcClaimMapper`, or name the claim with `tenant-context-key`.
- **`bifrost config-validate` reports a malformed connection string with constant text instead of naming the rejected keyword.**
- **`MetadataLoader(IConfiguration, string)` and `FileMetadataSource(IConfigurationSection, string)` are removed.** Use `new MetadataLoader(MetadataLoader.ReadMetadataRules(section))` or `new FileMetadataSource(MetadataLoader.ReadMetadataRules(section))`.

### Fixed — fifth review pass

- **A many-to-many field renamed to avoid a column (`roles` → `roles_2`) can be queried.** Its join was named after the target table, so reading it failed with "Unable to find queryField".
- **`<table>Pivot` honours `max-query-rows`, ordered by the row keys, and reports a cut with `truncated: true`.** It returned one row per group with no ceiling, so `rowKeys: [id]` read the whole table.
- **Pivot columns are aliased by position.** On PostgreSQL a label over 63 bytes was truncated as an identifier and its cells came back null; two labels sharing a 63-byte prefix failed the query. A missing pivot column now throws instead of reading as null.
- **`/_blob` answers 500 and logs at Error on a database fault.** Every `BifrostExecutionError`, outages included, answered an unlogged 404. Only access-denied and not-found still answer 404.
- **`/_blob` parses Range bounds and integer keys culture-invariantly.** `bytes=+0-` is ignored as malformed and `bytes=-0` answers 416, matching S3.
- **A pgwire numeric literal written with non-ASCII digits is refused with 42601.** It reached an unhandled `FormatException` and answered XX000 with a logged stack trace.
- **Name-based links inside a table prefix group resolve within the child table's schema first.** With `sales.app_*` and `archive.app_*`, `archive.app_order.customer_id` could link to `sales.app_customer`.
- **The junction in `many-to-many: Target:Junction` resolves in the source table's schema first and accepts `schema.table`.** It took the first table with that name in any schema; an ambiguous bare name now fails model load and lists the candidates.
- **Undoing a deferred change set replays its inverses newest first.** A TreeSync or batch that inserted a parent and then its children was undone parent first, hit a foreign-key violation, and stayed in `undoing` forever.
- **`policy-write-deny` (or `write-requires`) on the `tenant-filter` column no longer makes the table unwritable.** The policy check judged the tenant column the server had just stamped; it now runs first and judges only the columns the client wrote. A client that sends the tenant column itself is still refused.
- **Workflow `on-mutation` triggers receive the model.** No production call site set it on the observer context.
- **`limit: 0` returns an empty page with its total on SQL Server.** The root, link, flat-collection, grouped-aggregate and host read paths emitted `FETCH NEXT 0 ROWS ONLY`, which SQL Server rejects (error 10744). A zero window is now a `WHERE 1=0` statement on every dialect.
- **A fragment spread at the operation root (`query { ...F }`) resolves like the same fields written inline.** It threw while parsing and failed every root field in the document.
- **`_table(limit: null)` is treated as an omitted limit.** Only `limit: 0` returns an empty page.
- **SQL Server pivots on an empty label or one longer than 128 characters use the portable CASE WHEN path.** The native PIVOT named each label as an identifier and failed (errors 1038, 103).
- **History `entity_id`, outbox payload keys and the CloudEvents `subject` use one canonical form per key column type.** The first history key fix converted every unmodelled type through `string`, which rolled back writes on `datetimeoffset`/`time` keys and dropped fractional seconds from `DateTime` keys; the outbox recorded `_primaryKey` text as sent (`"05"`, `"5.0"`). Trail rows written before this release keep their old `entity_id` spelling and are not migrated, so an erasure by the canonical key does not reach them; purge those by their recorded spelling.
- **A workflow whose bare trigger table name exists in more than one schema is skipped with a warning.** The ambiguity threw inside the trigger loop and stopped the other workflows matching the same write.
- **A response key selected more than once (written twice, or inline plus a fragment spread, at the root or nested) returns the union of the selections.** The later occurrence's fields were dropped or failed with "Unable to find queryField".
- **A nested paged collection with `limit: 0`, or an offset past a parent's rows, reports that parent's real `total` with empty `data`.** It reported `total: 0`.
- **A SQL Server pivot whose labels differ only by trailing spaces uses the portable CASE WHEN path.** The native PIVOT failed with error 8156.
- **`bifrost config-generate` output with created-by/updated-by/deleted-by populators loads.** It now emits `:root { user-audit-key: id }` and asks you to confirm the claim.
- **The shipped Host config stamps actor columns from the `id` the identity mapper writes.** It named `username`, which no mapper writes, so every actor was NULL.
- **The ambiguous workflow trigger-table warning is logged once per workflow per model, not on every write.**
- **`pnpm test:js` builds the tested packages' workspace dependencies first,** so a stale `dist` no longer produces false failures.
- **`bifrost config-validate` reports malformed config JSON, non-string rules and a bad connection string as errors, and says when the model was not validated.** They crashed the tool with a stack trace. The connection string is never echoed.
- **Computed-column providers no longer run for the extra rows a nested paged collection fetches only to keep its total.**
- **SQL Server pivot labels equal only under accent, kana or width folding use the CASE WHEN form** instead of failing with error 8156 under an accent-insensitive collation.
- **Docs: `tenant-context-key` names the user-context key the tenant filter reads.** It does not rename the mapped `tenant_id`; a missing or multi-valued claim is refused. Under `DisableAuth` an unauthenticated write stamps NULL actors; no placeholder identity is assigned.
- **A host-supplied `JsonElement` under `user-audit-key` is refused when it is an array or object and stamped as its value when it is a scalar.** Every `JsonElement` reached the driver as a struct it could not bind.
- **The state-transition audit row records the tenant from the model's `tenant-context-key`.** It read a hard-coded `tenant_id`.
- **SQL Server pivots with non-ASCII labels no longer fail with error 8156 under invariant globalization.** Culture-aware folding is unavailable there, so a label set that may collide under some collation uses the CASE WHEN form.
- **`config-validate` and `doctor` name a wrong-shaped config file (non-object root or section, non-string metadata entry)** instead of calling it invalid JSON; `doctor` crashed on it.
- **`IGrantResolver` receives the identity's `TenantId` again.** An earlier fix in this pass made it always null.
- **The deferred review queue fails fast on a blank `tenant-context-key`** instead of scoping by `tenant_id`, matching the tenant filter.
- **Claim types that differ only in case no longer suppress the state-transition audit row or workflow triggers.** The HTTP user context is case-insensitive and merges such claims.
- **A scalar or object-shaped `BifrostQL:Metadata` fails startup and `config-validate`.** It was dropped silently, taking any tenant-filter rule with it. The Host, file metadata source and `config-validate` share one reader.
- **`doctor` and `serve` report a malformed or wrong-shaped config file** (including a non-object `connectionStrings` or non-string `bifrost` value) instead of crashing.
- **`<t>History` on a tenant-filtered table works when `tenant-context-key` is fed by a raw claim.** The trail bound the claim's array as the filter value and failed with a database error; a multi-valued claim is refused as access denied.
- **A tenant claim whose case differs from `tenant-context-key` resolves the same on every surface.** User-context copies dropped the case-insensitive comparer, so GraphQL reads refused what mutations accepted. Every copy goes through `UserContexts.Copy`.
- **`bifrost config-validate`, `doctor` and `serve` accept config files with comments and trailing commas, as the server does.**
- **Docs: `IGrantResolver` receives only the identity tenant (`tenant_id`), not the `tenant-context-key` value;** grants resolve before any endpoint's model is known.
- **The CLI (`serve`, `doctor`, `config-validate`) reads config keys case-insensitively, as the server does,** and refuses leaf keys that differ only in case. `"connectionstrings"` was reported missing and `"bifrostql"` was half-read.
- **`IGrantResolver` receives the principal's raw claims in `AppIdentity.Claims`,** so a per-tenant resolver can read a raw `org_id` claim on every transport.
- **`bifrost serve`, `doctor` and `config-validate` refuse a non-string connection string, `BifrostQL:Path` or `:Playground` in a config file, including colon-keyed properties such as `"ConnectionStrings:bifrost": 42`.** `"Path": 5` served the endpoint at `5`.
- **A host `IBifrostAuthContextFactory` that returns null gets the constant MCP authentication refusal,** and the affected Prometheus metric is excluded instead of the scrape faulting.
- **Docs: `AppIdentity.Claims` handed to `IGrantResolver` is the raw principal,** which can differ from the value the tenant filter scopes by; the guide names that value.

## 0.4.29 — 2026-11-22 (operation ids, set-based writes)

Tag `v0.4.29` · NuGet `0.4.819` · npm `@bifrostql/react` and `@bifrostql/types` `0.4.1`

### Security — silent fallbacks

- **An encrypted column on a table whose name exists in two schemas is masked again.** `CryptoReadProjector` looked the table up again by bare name. In a model with, say, `dbo.users` and `audit.users`, that lookup threw, and a catch-all returned the stored value unprojected: ciphertext, or legacy plaintext, went to callers who should have seen it redacted. `CryptoReadProjector.Project` now takes the resolved `IDbTable` instead of a table name.
- **A corrupt saved-object file fails the read instead of reading as absent.** `FileSavedObjectStore` treated a file it could not parse as missing. List and get hid it, and a version-0 create overwrote it with no conflict and no trace. Get, list and put now throw `InvalidDataException`, as `DbSavedObjectStore` already did for a corrupt row. `SavedObjectJson.TryDeserializeStored` is replaced by `DeserializeStored`.
- **An encrypted value that cannot be decrypted is still redacted, and now logged.** `CryptoReadProjector` redacted a value whose data key was not loaded or whose ciphertext failed to decrypt with no trace, so a missing key or corrupted column looked like an access rule. It now logs a warning through `BifrostErrorSink` once per column per read, naming the column and the reason (never the value). `BifrostErrorSink.Handled(site, detail, exception)` is the new seam for any failure handled by degrading.
- **A table or column hidden because its read policy failed to evaluate is now logged.** `SchemaReadVisibility` hid it fail-closed with a bare `catch`, so a policy that slipped past model validation made the table vanish from the schema with no explanation, even for an admin. It still hides it, and logs a warning naming the table (or column) through `BifrostErrorSink`.

### Breaking — silent fallbacks

- **The `JSON` scalar rejects text that is not valid JSON, in both directions.** A string variable or inline literal for a `JSON` argument must parse as JSON, or the request fails with a validation error; it used to be stored as-is. A stored value that does not parse now fails its field instead of being returned as a plain string, which a client could not tell apart from a real JSON string value. To store a JSON string, send it encoded (`"\"hello\""`).
- **`DbAuthConfig`, `DbAuthMode` and `DbAuthConnectionWrapper` are removed.** Nothing constructed them and no config key reached them, so the impersonation, per-user and session-context modes never ran. The wrapper also swallowed a failed `REVERT`, which would have handed a pooled connection back still impersonating the last user. No public replacement exists; per-request identity reaches SQL through the filter and mutation transformers.
- **`PhpSerializer` is removed.** Nothing called it: `EnablePhpSerialization` only marks PHP-serialized columns (`type: php_serialized`), and values were never decoded server-side. Its `Deserialize` also returned malformed input unchanged. `docs/WordPressSchemaBundle.md` claimed automatic decoding and now says to decode on the client, as the WordPress guide already did.

### Fixed — silent fallbacks

- **S3 `ListObjectsV2` logs a key it leaves out because its stored file pointer does not parse.** The object vanished from listings silently. `FileMetadata.FromJson` also catches only `JsonException` now, so a non-parse fault is no longer read as "no file".
- **A file upload whose row write fails, and whose orphan cleanup then fails too, logs the object left in storage.** `FileUploadResolver` swallowed the cleanup error. The caller still sees the original write error.
- **Tree sync logs each rerun after SQL Server picks it as a deadlock victim.** The retry was silent, so frequent deadlocks never showed up.

### Fixed — benchmark harness

- **`--bulk-paths` and `--transport` report a failing scenario and keep going.** Before, one throw (for example the MySQL floor without `AllowLoadLocalInfile`) aborted the rest of that provider's scenarios. Now a failed scenario prints a `FAILED` row, writes the error to stderr, and makes the process exit 1. The bulk harness also fails a bulk row that did not stream its staging load, adds `AllowLoadLocalInfile=true` to the MySQL connection string itself, and logs a failed drop of its throwaway database instead of swallowing it.

### Breaking — bulk staging visibility

- **`BulkBatchResult` gains a `StagingLoad` (`BulkStagingLoad`) member, and `StagedBulkBatchExecutorBase.LoadStagingAsync` is abstract and returns `Task<BulkStagingLoad>`.** A custom `IBulkBatchExecutor` reports whether it streamed its staging load, or why it did not. A subclass of the staged base streams through `LoadStreamingOrChunkedAsync`, passing a delegate that returns null on success or the reason the chunked load must run.

### Fixed — bulk staging visibility

- **A bulk batch that falls back from the streaming staging load now logs why.** Before, the MySQL and PostgreSQL streaming loads swallowed every failure and fell back silently, and a staging failure reran the batch on the per-row path silently. Now each case logs a warning under `BifrostQL.BulkBatch` (a successful stream logs at debug). The silence had hidden that the shared MySQL test container ran with `local_infile` off, so no test ever exercised MySQL streaming.
- **The MySQL test environment streams.** `docker-compose.test.yml` starts MySQL with `--local-infile=1`. CI sets `local_infile=1` at runtime. Both `BIFROST_TEST_MYSQL` strings (CI and `local-databases.runsettings`) carry `AllowLoadLocalInfile=true`. A new integration test fails on MySQL or PostgreSQL when the staging load does not stream. Recreate a running `bifrost-mysql` container to pick this up: `docker compose -f docker-compose.test.yml up -d mysql`.

### Security — write-path fixes

- An update or delete runs the mutation transformer chain before the pipeline answers by the request's shape. A caller the chain refuses used to get the shape's answer instead: `NO_UPDATE_COLUMNS` or `PARTIAL_PRIMARY_KEY` for a key-only or partial-key update, and a no-op `0` for an update with no key column (a keyless view) or a delete naming no column, which read as a write that ran. The chain's refusal is now the answer on the single-row, `_batch` and bulk paths; a write the chain passes is answered by its shape as before. An upsert that writes nothing (empty, or key-only on an existing row) and a batch upsert with a partial key run the update chain first too.
- An operation-id retry from a caller the policy now refuses gets the live `ACCESS_DENIED`, never `OPERATION_ID_REUSED`. The replay checked the recorded fingerprint before authorization, so a refused caller could tell which of its own ids held a record for another write.

### Breaking — write-path fixes

- The root table's `batch-max-size` (default 100) now bounds the writes of a `save` or `sync`, as it bounds a batch's actions. A larger tree is refused whole, before anything is written. Raise `batch-max-size` on the root table if your clients send larger trees.

### Added — write-path fixes

- `MutationTransformContext.Operation` names the server operation (`IBifrostOperation`) whose write this is, on every write a `BifrostOperationContext` makes, and is null on a generated write, including a sibling mutation field of the same request. Only the engine sets it, so a transformer can hold a rule for clients that an operation is exempt from (a column only an operation may set).
- `MutationTransformContext.MutationState` is the per-write bag both hook phases of that write receive as `MutationObserverContext.MutationState`, so a transformer that locked and loaded the row (`LoadRowForUpdateAsync`) hands it to the after-write hook instead of the hook reading it again. It is fresh per write: per field, per `_batch` action, per tree-sync node. `BifrostOperationContext.InsertAsync`, `UpdateAsync` and `DeleteAsync` take an optional `state` whose entries start that write's bag.
- `QueryTransformContext.Filter` is the node's own filter as the request gave it (`filter` merged with `_primaryKey`; null when it gave none), before any transformer adds to it, on top-level and nested nodes. A filter transformer can now refuse a read that pins one record outside the caller's scope (throw with its own code) instead of only narrowing it to an empty answer. The blind-index equality rewrite no longer edits the request's tree in place, so a transformer sees the column the client named; a history-trail read ANDs its entity pin after the transformer pass, so `Filter` there is the client's alone.
- `populate: host` column metadata marks a column the host's own mutation transformer stamps. A `NOT NULL` column so marked is optional in its table's insert input, so the schema keeps its constraint instead of going nullable to suit the generator; the audit module leaves it alone, the config validator accepts it, and an insert that reaches the database without a value fails on the constraint. `MetadataKeys.AutoPopulate.IsServerStamped` is the one rule both the schema generator and the validator read.
- `BifrostOperationContext.QueryAllAsync(table, where, columns, sort)` reads every row `QueryAsync` would (same caller, scope, guards, decryption, and the operation's transaction) with no row window, as one statement, so every row comes from one snapshot. `QueryAsync` and every client-reachable read keep `max-query-rows`.
- `:root { mutation-forms: … }` narrows every table's generated mutation surface to the listed write forms (`insert`, `update`, `upsert`, `delete`, `sync`, `save`, `delta`, `_primaryKey`, `batch`). An unlisted form's argument and input types leave the schema and `<table>_batch` exists only when `batch` is listed, so a client request naming one fails validation and writes nothing. Server writes (operations, `IMutationIntentExecutor`) are unaffected; without the key every form stays.
- `BifrostOperationContext.DeltaAsync(table, BifrostDelta)` writes many rows (`Inserted`, `Updated`, `Deleted`, the `delta` argument's shape) through the batch pipeline inside the operation's transaction, with no `BEGIN`/`COMMIT` of its own, and returns the rows affected. The transformer chain runs per row with the write's connection, a refusal of any row refuses the call, and `batch-max-size` / `bulk-batch-threshold` do not apply (they bound a client's request).
- `IInTransactionSetMutationHook`: an in-transaction hook called once per write with every written row's verb, data, key and `MutationState` (`MutationSetContext`, `MutationSetRow`), instead of once per row: one row for a single-row write, every changed row of a `<table>_batch`, a `delta` or a `DeltaAsync`. Per-row hooks keep their per-row calls, each before the next row's statement.
- On PostgreSQL the batch pipeline's per-row loop writes a run of adjacent inserts as one `INSERT … RETURNING` when no per-row in-transaction hook applies to the table, so a table whose hooks are all set-aware keeps its insert runs in one statement. Inserts that satisfy a self-referencing foreign key only together (`[insert child, insert parent]`) now pass on that loop too.
- `save` and `sync` write level by level: on PostgreSQL the new nodes of one table at one depth are one `INSERT … RETURNING`, keys mapped back to their own children, unless a per-row in-transaction hook applies to the table. Set-aware hooks are called once per level.

### Added — database errors

- A statement the database ends at its own time limit (PostgreSQL `statement_timeout`, SQLSTATE `57014`, or a driver command timeout) rolls its transaction back, and the GraphQL HTTP middleware throws `BifrostStatementTimeoutException` to the host with nothing written, in place of a `DATABASE_ERROR` under HTTP 200. That holds whether BifrostQL, a transformer or a hook issued the statement. A host middleware ahead of BifrostQL answers it with its own status (a 503, say); a request cancelled by its own token never raises it. Every other front door answers with the new code `STATEMENT_TIMEOUT` (`BifrostExecutionError.StatementTimeoutCode`). gRPC answers it `DEADLINE_EXCEEDED` with fixed text.
- A host answers a constraint violation with its own code and sentence: register an `IDatabaseErrorMapper` with `AddBifrostDatabaseErrorMapper<T>()`. The mapper gets the kind (unique, foreign key, check, exclusion, from the SQLSTATE), the constraint's name and the table, on every GraphQL mutation field (a generated write, a `_batch`, a tree write, a server operation). The error it returns replaces BifrostQL's answer, and a null keeps it. `BifrostExecutionError.ConstraintViolation` carries the violation server-side.

### Breaking — operation ids

- **The operation-id store gains a `field_name` column, and its unique key becomes `(tenant, caller, operation_id, field_name)`.** A record now belongs to one mutation field, keyed by the field's response name (its alias, else its name). One `Idempotency-Key` header can therefore cover a request of several mutation fields, and each field records and replays on its own; before, the second field answered `OPERATION_ID_REUSED` and wrote nothing. A retry must keep the fields' aliases. Model load refuses a store without the column or the key. To migrate, add `field_name` (text, not null; on MySQL `VARCHAR(128)` keeps the key under InnoDB's 3072 bytes), backfill existing rows with the field each one recorded (the table's field name, or `<table>_batch` for a batch), and widen the key.

### Added — operation ids

- `operation-id: none` opts a table out of operation ids, as no value does, so a later metadata rule can take a table out of a wildcard one: `public.* { operation-id: required }` then `public.operation_ids { visibility: hidden; operation-id: none }`. A hidden table accepts only `none`.
- `:root { operation-id: optional | required }` records every mutation server operation (`IBifrostOperation`) under the request's `Idempotency-Key`, in the operation's own transaction. A retry with the same arguments answers the recorded result (an object result included) and runs nothing; other arguments answer `OPERATION_ID_REUSED`; a run that throws records nothing. Under `required` an operation without a key is refused with `OPERATION_ID_REQUIRED` before it runs. Writes a recorded run makes reach an `operation-id: required` table.
- `@bifrostql/react`: `useBifrostMutation` sends an `Idempotency-Key` on every call and repeats it on the same instance's next call after an unanswered one (failed fetch or non-2xx status), so a lost response is not applied twice.

### Breaking — fourth review pass (LDAP, RESP)

- **RESP write intents name the table schema-qualified** (`dbo.users`, was `users`), so a write lands on the table the key resolved to when two schemas share a name. Custom `IMutationIntentExecutor` implementations that match on the bare name must resolve the qualified one through `IDbModel.TryGetTableFromClientName`.
- **RESP write errors no longer name the table or the stored column**: `-ERR unknown column '<name>'`, `-ERR column '<name>': ...`, `-ERR cannot set primary-key column '<name>'` echo only the name the client sent.
- **LDAP refuses a connection over `MaxConnections` with `busy` (51)**, was `unavailableCriticalExtension` (12), which reports an unsupported critical control.
- **The LDAP decoder refuses an undefined search `scope` or `derefAliases`** (`protocolError`). Out-of-range values were cast silently, so `2^32` read as `baseObject`.

### Security — fourth review pass (LDAP, RESP)

- LDAP and RESP writes are bounded. A peer that stops reading parked the response write forever, holding its admission slot: 100 such sockets filled the cap. Pre-auth writes now end with the pre-auth deadline, and every write after authentication ends at the new `WriteTimeout` (default 1 minute) on `LdapWireOptions` and `RespWireOptions`. The connection closes and its slot is released.
- Pre-auth replies no longer echo large client strings. RESP's unknown-command reply caps the echoed name and arguments at 128 characters and replaces CR/LF so an argument cannot forge a second reply; LDAP's unsupported extended operation answers a fixed text instead of the request OID.
- LDAP `member` / `memberOf` reads run under the model's row ceiling with a total ORDER BY. A membership leg cut by the ceiling ends the search with `adminLimitExceeded` instead of serving a short value list.
- LDAP StartTLS sessions send close_notify and dispose the `SslStream` when the session ends. The stream was never disposed, so the peer saw a truncated TLS stream.
- LDAP bind passwords are wiped from every buffer they pass through: the message body after decode, the array that body outgrew, the connection read buffer, and the decoded copy on every bind path, including a listener with bind authentication disabled.
- LDAP: a malformed stored password hash fails the bind as invalid credentials, on the same decoy verification path as an unknown user, and logs a warning. It used to throw out of the bind.
- RESP: peers with no known address share one rate-limit bucket instead of each getting a fresh one.

### Fixed — fourth review pass (LDAP, RESP)

- RESP `MGET` groups keys by the resolved table, not its name, so `MGET` across two same-named tables in different schemas reads each table its own keys.
- RESP read lookups filter by read visibility before checking ambiguity: a name shared by two schemas resolves to the only readable one, and answers `unknown table` when neither is readable.
- LDAP `sizeLimitExceeded` is reported with a cap+1 sentinel: a search returning exactly the size limit is complete, and only an entry past it marks the result partial.

### Breaking — third review pass

- **S3 errors are mapped by condition code, not exception type.** Only a missing or unreadable address (`NOT_FOUND`, `ACCESS_DENIED`) answers `404`; a write conflict (`CONFLICT`, `EXCLUSION_VIOLATION`) answers `409 OperationAborted`; anything else (pool exhaustion, a disposed service, a database fault) answers `500 InternalError` and is logged at Error. Before, every `InvalidOperationException` and `BifrostExecutionError` became `404` and was logged at Debug. `DeleteObject` no longer answers `204` when the row is visible but nothing was deleted: a zero-row write is `404`, a conflict `409`, a fault `500`. `FileObjectSeam` tags its own not-found conditions with the new `BifrostExecutionError.NotFoundCode` (`NOT_FOUND`) instead of throwing `InvalidOperationException`.
- **S3 SigV4 requires every `x-amz-*` request header to be signed.** A request carrying an unsigned `x-amz-*` header (including `x-amz-content-sha256`, `x-amz-copy-source`, `x-amz-meta-*`) is refused with `403 AccessDenied`. Clients that sign with the AWS SDKs or rclone already do this.
- **S3 continuation tokens expire** after `S3Options.ContinuationTokenTtl` (default 15 min). The token format changed (`s3ct2`), so tokens issued before the upgrade are refused once.
- **Stored-procedure config is removed.** BifrostQL never loaded or executed stored procedures; the keys were accepted and did nothing. `sp-include`, `sp-exclude` and `stored-procedure-role` are now unknown `:root` keys, so a model that still sets one fails at load: delete them. `IDbModel.StoredProcedures`, `DbStoredProcedure` and the unused `MultiDbConfig` / `DatabaseFieldConfig` / `CrossDbJoinConfig` types are removed, and the `DbModel.FromTables` overloads lose their stored-procedure parameter. Nothing public replaces them; multi-database hosting stays on `BifrostMultiDbOptions`.
- **`Decimal` is sent as a decimal string** (`"19.99"`), not a JSON number, so values past a double's 15–17 digits keep every digit. Input still accepts a number or a string. Clients that read `Decimal` fields as numbers must parse the string. On `<table>Aggregate`, `_sum`/`_avg`/`_min`/`_max` over a decimal column are typed `Decimal` instead of `Float`. The proto schema maps `decimal` to `string`, so `@bifrostql/codegen` emits `string` for it.
- **Per-row `_agg` returns `AggregateNumber`, not `Float`,** written by the same rule as `<table>Aggregate`: `count` is a JSON integer, and `sum`/`avg`/`min`/`max` over a decimal or bigint column is an exact decimal string (`"1029.98"`); other numeric columns stay JSON numbers. Typed `Float`, a bigint SUM past 2^53 or a NUMERIC(38,10) SUM reached the client rounded. Clients that read `_agg` over a decimal or bigint column as a number must parse the string. On `<table>Aggregate`, a bigint column's `_sum`/`_avg`/`_min`/`_max` field is now `Decimal` instead of `Float`, for the same reason.
- **`@bifrostql/codegen` maps proto `bytes` to a base64 `string`** (was `Uint8Array`), matching what the server writes into JSON.
- **Binary transport:** a client that stops acking a chunked response loses only that response, not the connection. `ChunkSender.DefaultAckTimeout` is now 60 s (was 30 s) and is the default for `UseBifrostBinary(ackTimeout:)`, the `BifrostBinaryMiddleware` constructors and `ChunkSender`. When it expires the server drops the stalled response with a `CHUNK_ACK_TIMEOUT` Error frame and keeps the connection open (it used to close it). To wait as long as the consumer takes, pass `ChunkSender.NoAckTimeout`.
- **EAV `_meta` is capped per parent, and the cut is reported on a sibling `_metaTruncated: Boolean` field.** `_meta` returns at most `max-query-rows - 1` attributes per parent, windowed in SQL, the first in key order; before, it returned every attribute row however many there were. `_metaTruncated` is true for a parent that has more. `_meta` reserves no key: the earlier unreleased build marked a cut with `"__truncated": true` inside `_meta` and refused an attribute keyed `__truncated`, which failed the whole page. Clients that looked for that key select `_metaTruncated` instead.
- **S3 `ListObjectsV2` seeks in key order in SQL, and `S3Options.MaxListMaterialize` is renamed `MaxInMemoryListKeys`.** Each page orders by the object key computed in SQL (binary collation, so UTF-8 order on every engine) and reads `max-keys + 1` rows after the resume position, instead of loading up to 10,000 rows and sorting them in memory. The SQL key matches the server's key byte for byte for every key type the engine can render exactly; rows without a file are filtered in the same query. A bucket whose keys sort in SQL lists completely at any size, and a full walk hands each row to the server about once. On SQLite in memory, page 1000, a full walk of 1000/5000/9999 rows went from 16/773/2678 ms to 10/80/183 ms. Without an index on the key expression the database computes it for every row on each seek; the S3 guide gives the index statements for PostgreSQL, MySQL and SQL Server (on PostgreSQL 16 the index took a 9999-row walk from 163 ms to 117 ms). **Float-key buckets still sort in .NET, bounded by `MaxInMemoryListKeys`**: SQL Server `float`/`real`, SQLite columns with REAL affinity, MySQL `FLOAT`, and decimals with a declared scale above 28 have no exact text in SQL, so the server picks the strategy from the key type when it loads the model: each page reads the bucket's keys (file rows only), formats them in C#, and sorts them, as every bucket did before. Such a bucket with more than `MaxInMemoryListKeys` file rows (default 10,000, narrowed by `max-query-rows`) is refused with `InvalidArgument`, never listed short. Rename `MaxListMaterialize` to `MaxInMemoryListKeys` in your `S3Options` setup; it no longer bounds buckets whose keys sort in SQL, and a value below 1 now fails at construction. A page with a delimiter runs at most 32 seeks and may come back short, even empty, with `IsTruncated=true`. Float keys need PostgreSQL 12+ and MySQL 8.0.14+; no other version floor.
- **`TreeSyncStateLoader.LoadAsync` takes an open `DbConnection`** instead of an `IDbConnFactory`; tree-sync now loads its diff inside the write transaction.
- **The AppMetadata, Blob and SavedObjects mounts inherit the endpoint's `DisableAuth`** through `MountAuthRequirement`. An anonymous endpoint now serves them anonymously unless `RequireAuth = true`; with no identifiable endpoint they fail closed.
- **Local auth keeps server-side sessions.** Cookies issued before the upgrade carry no session id, so every user signs in again once. Sessions end after 15 idle minutes by default (8 h absolute is unchanged). A role, tenant or household change ends the user's sessions instead of updating them.
- **`AddBifrostLocalAuth` needs DDL and write rights** for its `_bifrost_auth_*` tables, created at host start. Missing rights fail the start, not the first login. Register your own `ILocalAuthStateStore` to keep them elsewhere.
- **Local password hashes use PBKDF2-HMAC-SHA512 at 210,000 iterations** (2.1× the work per login). Existing hashes upgrade at the next sign-in.
- **`LocalAuthOptions.PasswordHashIterations` below 210,000 fails at startup** (was below 100,000). `MinPasswordHashIterations` now equals the OWASP figure, so configuration can no longer weaken the default. Tests that need cheap hashing pass an `IPasswordHasher<string>` to the `LocalUserStore` constructor.
- **Local auth requires a TOTP second factor by default** (`LocalAuthOptions.RequireMfa = true`; PCI DSS 8.4.2 / 8.5.1, HIPAA 164.312(d)). `AddBifrostLocalAuth` now fails at startup unless you either set `MfaKeyRef` (e.g. `"config:mfa"`) and register an `IRootKeyProvider` plus a durable `IDataEncryptionKeyStore` to seal the secrets, or opt out with `RequireMfa = false` (or require it only for some roles with `RequireMfaForRoles`). With MFA on, `POST /auth/login` returns `200` `{"status":"mfa-required","challenge":...}` with no cookie for an enrolled user, who finishes at `POST /auth/mfa/verify` with a code or recovery code; a user not yet enrolled gets `200` `{"status":"mfa-enrollment-required"}` and a session that reaches only `/auth/mfa/enroll`, `/auth/mfa/enroll/confirm` and logout (the binary WebSocket closes and a `/_chat` stream ends on it). Login clients must handle the `200` bodies. `POST /auth/password` for an enrolled user also needs `code` or `recoveryCode`. New endpoints: `/auth/mfa/enroll`, `/enroll/confirm`, `/verify`, `/disable`, `/recovery-codes`, and `{AdminPath}/reset-mfa`. Sessions record `amr` (`pwd`, `otp`, `recovery`) as claims. `ILocalAuthStateStore` gains MFA and challenge methods, so a custom store must implement them; two new tables, `_bifrost_auth_mfa` and `_bifrost_auth_mfa_challenges`, and two new `_bifrost_auth_sessions` columns (`amr`, `mfa_pending`) are created at host start. A development database whose sessions table predates them must drop `_bifrost_auth_sessions` once.
- **OData refuses Basic credentials on plain HTTP** with 403 ("Basic credentials are accepted only over HTTPS.") before reading them. Set `ODataOptions.AllowCleartextBasicAuth` to opt out (off by default; a startup warning when on). `X-Forwarded-Proto` counts only when the host configures ForwardedHeaders.
- **A read-denied table answers like a missing one** on RESP (`-ERR unknown table`, was `-NOPERM`), pgwire (42601 `relation "<name>" does not exist.`, was 42501), LDAP (`noSuchObject`, and subtree searches skip denied families; was `insufficientAccessRights`) and gRPC (UNIMPLEMENTED "Method is unimplemented.", was INVALID_ARGUMENT / PERMISSION_DENIED). OData, MCP, S3 and feeds already matched and are now pinned. GraphQL and the binary transport still differ: the schema is not filtered per identity.
- **SQL Server `timestamp` / `rowversion` columns are row versions, not date-times.** They are exposed as base64 binary, left out of insert input, and usable as a `concurrency-token`. Postgres, MySQL and SQLite `timestamp` keep their date-time meaning.
- **Field-encryption key APIs are async.** `IDataEncryptionKeyStore` has only `LoadAsync`/`StoreAsync`, so custom key stores must implement them. `EnvelopeKeyManager` key methods are `*Async` (`GetDataKeyAsync`, `RotateAsync`, …), `CryptoReadProjector` is built with `CreateAsync`, and `TableReadChain.For` is `ForAsync`. A key version minted by another process is picked up within about a second.
- **`RowScopeCompiler.Compile` takes an `ITypeMapper`**, so row-scope comparisons convert context values by the column's dialect type.
- **`AddBifrostMcpTools(Stream)` is replaced by `AddBifrostMcpTools(Func<CancellationToken, Task<Stream>> openStream, string description = "stream")`.** The removed overload copied the whole stream synchronously inside the source's constructor, at registration. The factory runs once, when the document loads at host start, and the loader disposes the stream it returns. `StreamDeclarativeToolDocumentSource` takes the same factory. Migrate `AddBifrostMcpTools(stream)` to `AddBifrostMcpTools(_ => Task.FromResult(stream))`, or better, open the stream inside the factory.
- **Declarative MCP tool documents load at host start, with async I/O.** `AddBifrostMcpTools` no longer reads the document; a missing or malformed one fails `StartAsync` instead of the registration call. `IDeclarativeToolDocumentSource.OpenRead()` is now `OpenReadAsync(CancellationToken)`, `DeclarativeToolDocumentLoader.Load()` is `LoadAsync(CancellationToken)`, and `DeclarativeToolDocument` is no longer a DI service: resolve `DeclarativeToolDocumentProvider` and await `GetAsync`.
- **A `DbSavedObjectStore` table name must be `_bifrost_saved_objects` or start with `_bifrost_saved_objects_`.** Any other configured name (including `_bifrost_saved_objectsx`) throws `ArgumentException` at construction. Rename a custom table, for example `_bifrost_saved_objects_tenant_a`.
- The SQL Server metadata tables (`_bifrost_config`, `_bifrost_app_metadata`) are hidden from every served model, except `_rawQuery`, which runs caller SQL and is opt-in (`raw-sql: enabled`) and role-gated (`raw-sql-role`). They sat in the served database, so a client could read or rewrite the tenant, soft-delete and policy metadata of the API it was calling. `DatabaseMetadataSource` and `DatabaseAppMetadataSource` now refuse a configured table name outside those families (breaking for custom names: rename the table to the family name or the family name plus `_`, e.g. `_bifrost_config_staging`). A family is the exact name or the name followed by `_`, so a user table such as `_bifrost_configuration` stays served.

- **gRPC: the gRPC routes answer only on `GrpcWireOptions.Port`, and that port serves only gRPC.** A gRPC call on any other listener, and any non-gRPC path (`/graphql`, OData, S3, …) on the gRPC port, gets HTTP 404. Hosts that reached gRPC through their main HTTPS port must point clients at the gRPC port.
- **gRPC status changes:** an authorization denial on `List`/`Stream`/writes is UNIMPLEMENTED "Method is unimplemented." (was PERMISSION_DENIED), so it answers like a table the caller cannot see; a write conflict (unique-key collision) is ABORTED (was INTERNAL); an `Update` with no non-key column is INVALID_ARGUMENT (was INTERNAL); protobuf framing that does not parse is INVALID_ARGUMENT (was INTERNAL with an Error log).
- **pgwire: portals close at `Sync` and on a simple query,** the end of the implicit transaction as in PostgreSQL. A row-limited `Execute` resumes only inside the same `Sync` batch; a later `Execute` of the portal answers 26000. Named prepared statements still live for the session.
- **pgwire: `SELECT *` omits read-denied columns,** and naming one answers 42703 `column "<name>" does not exist`, like a missing column (was 42501).
- **`ILocalAuthStateStore` has three new members** a custom store must implement: `RebindSessionCredentialStampAsync`, `ClearExpiredLockoutAsync` and `ClearFailuresUnlessLockedAsync`. The two clears are conditional: each deletes the lockout row only while it is still the one the caller checked, and says whether it did.
- **`LocalUserStore.VerifyCredentialsAsync` no longer rehashes.** The login endpoint (`POST /auth/login`) upgrades the hash after its lockout check. Nothing public replaces the old side effect: a host that verified credentials directly and relied on the upgrade signs users in through the endpoint.
- **`LocalAuthOptions.PasswordHistoryCount` counts the passwords before the current one.** N=4 refused the current password and only three before it; it now refuses the current one and the four before it (PCI DSS 8.3.7), so the history keeps N+1 hashes.
- **`LocalAuthOptions.SessionLifetime` and `LockoutDuration` above 3650 days (`LocalAuthOptions.MaxDuration`) fail at startup.** A larger value overflowed `DateTimeOffset` at the first sign-in or lock.

### Security — third review pass

- S3 listings project only the key and file columns the caller may read (via `SchemaReadVisibility`). A read-denied file column used to make the whole bucket fail while `ListBuckets` still showed it; now its objects are left out and the readable file columns list. A bucket whose key column or every file column is read-denied is hidden from `ListBuckets` and answers `NoSuchBucket`.
- S3 SigV4: an unsigned `x-amz-copy-source` could be added to a captured signed `PutObject` and replayed as a `CopyObject`. Every `x-amz-*` header must now be signed (see Breaking).
- S3 continuation tokens carry their issue time inside the MAC and expire (see Breaking).
- OData Basic refunds the attempt slot of a login whose password verifies, on both the per-source and per-account window. The caps now count only failures, so Power BI or Excel paging past 100 requests a minute is no longer refused with `429`, and a third party can no longer fill a user's account window from that user's own traffic. `ProtocolAuthAttemptLimiter` gains an atomic refund that only credits the window the slot was taken in.
- gRPC: the gRPC routes used to answer on every Kestrel listener that spoke HTTP/2, bypassing the gRPC port's loopback bind, TLS requirement and connection cap, and the gRPC port ran the whole host pipeline. Both directions are now refused by the accepting socket's local port, never the `Host` header. Registering the adapter also no longer unbinds the host's own URLs (`UseUrls`, `ASPNETCORE_URLS`): Kestrel drops them once a listener is declared in code, so they are now carried onto the server alongside the gRPC listener.
- gRPC: an anonymous call to an unregistered method answered UNIMPLEMENTED while one to a real table answered UNAUTHENTICATED, listing the tables to a caller with no credential. Unregistered methods now authenticate first.
- pgwire: new `WriteTimeout` (default 60 s) bounds every post-auth write. A client that sent a query and never read the result held its connection slot forever.
- pgwire: new `MaxSessionStatementBytes` (8 MiB of SQL) and `MaxSessionPortalBytes` (32 MiB of suspended rows) cap per-session memory by bytes. The count caps alone left the bytes loose: 200 statements of up to 1 MiB of SQL each, and a full cached result on every portal. Over a budget, 53400 and the session survives.
- pgwire: a length field of 0x7FFFFFFF in a SASL response (pre-auth) or a Bind value overflowed its bounds check and let `ArgumentOutOfRangeException` escape the connection handler. Both now answer 08P01.
- pgwire: an `SSLRequest` after TLS is up is refused with 08P01 instead of starting TLS inside TLS.
- pgwire: SCRAM validates the GS2 header (`n,,` or `y,,`, no authzid) and requires `c=` to echo it; a mismatch is 08P01 even with a valid proof.

- Credential rate limits are reserved atomically. The shared per-source and per-account limiter (pgwire, RESP, LDAP, OData Basic, local login) read and incremented each window as two steps, so concurrent attempts overshot the cap; 32 simultaneous guesses against an account cap of 2 were all admitted.
- Local auth: password history orders by insertion, not by timestamp, so the last-four reuse check holds when changes share a clock tick.
- GraphQL variables refuse a JSON number token longer than 1,024 characters (`ExactNumericScalars.MaxNumberTextLength`) with 400 "The request body could not be parsed." before parsing it, and a `BigInt` value longer than that is refused as invalid. `ExactNumberInputsJsonConverter` handed an all-digit token of any length to `BigInteger.Parse`, whose cost grows faster than the digit count, so the parse was bounded only by the request body size.
- The saved-object table is never a GraphQL table. `_bifrost_saved_objects` (and any configured `_bifrost_saved_objects_*` name) was exposed as a GraphQL table when the store shared the served database, so every owner's saved objects were readable through it. It is now hidden from every served model, like the `_bifrost_auth_*` tables. The exception is `_rawQuery`, which runs caller SQL and is opt-in (`raw-sql: enabled`) and role-gated (`raw-sql-role`): a caller allowed to use it can still `SELECT` these tables.
- LDAP: new `MaxMembershipValuesPerSearch` (default 10,000) caps member/memberOf values across one search; over it the search ends with `adminLimitExceeded` instead of cutting values.
- Local auth: logout, password change, disable, delete and admin action (`POST /auth/admin/revoke-sessions`) revoke sessions server-side; `MaxConcurrentSessions` caps them.
- Local auth: an account locks after 10 consecutive failed logins for 30 minutes (`LockoutThreshold`, `LockoutDuration`; null waits for `/auth/admin/unlock`). This is separate from the rate limiter.
- Local auth: `/auth/password` enforces at least 12 characters with a letter and a digit, refuses the current password and the 4 before it, and supports opt-in `MaxPasswordAge`.
- Local auth: sign-in, logout, lockout, password and session events go to an audit trail (`IAuthAuditSink`, log category `BifrostQL.Audit.Authentication`) with no secrets.
- Blob and chat-media size caps are measured in the database (`DATALENGTH` / `LENGTH`) before the value is read.
- New guide: [Compliance controls](/BifrostQL/guides/compliance-controls/) maps HIPAA, SOX and PCI DSS requirements to shipped controls and names each gap.
- Local auth: a session ended by logout, an administrator, a password or privilege change, or expiry stops an open binary WebSocket (`/bifrost-ws`) at its next message, closing it with PolicyViolation, and stops a `/_chat` stream before its next tool call with a `session-ended` error. Both kept running on the identity checked when they opened. All three surfaces run the one check, `LocalAuthEndpoint.RevalidateSessionAsync`.
- Local auth: the login rehash replaces only the hash it just verified (`AND password_hash = @verifiedHash`), runs after the lockout check, and keeps the user's other sessions. A rehash racing a password change could overwrite the new password, and a locked account's password was still upgraded.
- Local auth: an unknown login and a stored hash below the configured iteration count spend the same PBKDF2 work as a current hash, so response time no longer tells an unknown account or a legacy hash apart.
- Local auth: a lapsed lock is cleared only while it is still the lapsed one, and `AccountUnlocked` is audited only when a row was actually deleted. A correct password or password change no longer clears a lock taken a moment after its own lockout check.
- Local auth: rate-limited login and password-change refusals (429) are audited as failed attempts with reason `rate-limited`, without the login name typed.
- Binary WebSocket transport (`/bifrost-ws`): every frame the server sends now has a write deadline (`UseBifrostBinary(writeTimeout:)`, default 30 s). A peer that stops reading is aborted and its connection slot freed, where before it held the slot for ever. A client that floods `ChunkAck`s, or `ChunkNack`s a sequence past the last chunk, is closed with `ProtocolError` instead of blocking the reader or growing server state. A reassembled upload stays charged against the 128 MiB per-connection reassembly cap until its request ends, so 16 concurrent 64 MiB uploads can no longer hold about 1 GiB. A refused first upload chunk no longer takes one of the 16 pending-upload slots. The read deadline now sends the `PolicyViolation` close frame before dropping the peer (it used to abort the socket first), and the read, write, close-handshake and ack deadlines run on the registered `TimeProvider` (the upload-reassembly and retransmit-buffer expiry still use the system tick count).

### Fixed — third review pass

- S3 `PutObject` bodies between Kestrel's 30 MB default and `S3Options.MaxBodyBytes` (64 MiB) failed with `500`. The endpoint now raises the request's `IHttpMaxRequestBodySizeFeature` to `MaxBodyBytes`.
- S3 `max-keys` and `X-Amz-Expires`, and OData integer `$filter` literals, parse with an explicit `NumberStyles` and the invariant culture, so the thread culture cannot change what is accepted.
- Two concurrent resumes of the same deferred undo no longer both run: one claims the change set under a lease and the other gets `CONFLICT`.
- Tenant, row-scope and writable-values comparisons convert context values by the dialect's column type, so Postgres `int4` or SQL Server `money` bind as numbers instead of strings.
- The MCP bearer token exchange is awaited and honours the tool call's cancellation; `CreateServerOptions` accepts an async `userContextProviderAsync`.
- Field-encryption keys load once at the start of a request on the async path, so a DB- or KMS-backed key store no longer blocks request threads. The wire adapters' pipe stream no longer offers blocking sync reads and writes.
- gRPC insert reports the real `AffectedRows` (0 when a trigger drops the row), and write events fire only when rows land.
- Numbers inside native JSON columns (Postgres json/jsonb, MySQL json) keep their exact text on read and write.
- Per-row `_agg` over a decimal column answers a value no decimal holds (SQLite text such as `'N/A'` in a DECIMAL column, or a SUM/AVG past decimal's range) with an `AggregateNumber` serialization error that names no cell data, as `<table>Aggregate` refuses the same values. It threw a raw `FormatException` carrying the cell text, or an `OverflowException`.
- A `Decimal` output field (a column read, or `<table>Aggregate`'s `_sum`, `_max` and the rest) whose cell no decimal holds is refused with a serialization error that names no cell data, through the conversion `_agg` uses. Before, text such as SQLite's `'N/A'` in a DECIMAL column answered `Unable to convert 'N/A' value of type 'String' to the scalar type 'Decimal'`, carrying the cell, and a double past decimal's range (SQLite `1e300`) threw a raw `OverflowException`.
- A number in a `JSON` column variable with more digits than a decimal or double holds (`0.1234567890123456789012345678901234567891`) is stored as sent. GraphQL.SystemTextJson's variable reader parsed it to a decimal, rounding it to 28–29 digits; `AddBifrostQL` now registers `ExactNumberInputsJsonConverter`, which keeps such a number as its own JSON token. `Float` and `Decimal` variables still accept it and round it as before. A host that builds its own `GraphQLSerializer` adds the converter to keep the fix.
- Tree-sync reads the existing rows under an update lock inside its write transaction, so a concurrent writer can no longer change the diff between load and write.
- Binary transport: Error frames carry a machine-readable `error_code` (protobuf field 13); the client requeues concurrency refusals by code. A stream no longer times out while its consumer still holds received chunks. Requests over 4 MiB are sent as chunks the server reassembles (up to 64 MiB); larger ones are refused before sending.
- edit-db: grouping reports truncation under any `max-query-rows`, including ceilings below 1001, and names the real group count.
- edit-db: a Boolean key, filter or field value other than true/false/0/1 is refused with a clear error instead of being read by truthiness.
- edit-db: a composite foreign key's header filter selects one parent row and filters on every FK column; the `cf` URL parameter adds a `_fk_tuple` form and still reads old links.
- edit-db and `@bifrostql/react`: grouping, sort, aggregates and number/percent formats read `Decimal` strings exactly.
- Local auth: a login, password-change or administration request whose body is not JSON (another content type, or an undecodable charset) answers 415 instead of 500.
- Local auth: the administration endpoints refuse a `userId` longer than 128 characters with 400 before acting; it could not fit the audit column, so the action ran and the audit write failed after it.
- Local auth: the email claim is the login column's stored value, not the caller's spelling of it under a case-insensitive collation.
- SQL Server: a tree sync's child read takes `WITH (UPDLOCK, HOLDLOCK)` instead of `WITH (UPDLOCK)`. The update lock covered only the children it read, and a concurrent child INSERT's foreign-key check takes a shared lock on the parent, which an update lock does not block, so a new child could land between the sync's diff and its commit. The key-range lock now holds it until the sync commits. The lock covers the range the read's plan scanned: an index seek on the foreign key locks that parent's key range, and a scan (no such index, or a small table) locks the whole child table, so every child insert on that table waits for the sync. PostgreSQL and MySQL are unchanged. A sync chosen as a deadlock victim (SQL Server 1205, PostgreSQL 40P01, MySQL 40001) reruns its whole transaction, up to three runs, then fails with the last deadlock.

### Changed — third review pass

- EAV `_meta` reads a whole page's attributes in one query per 500 parents instead of one per row (1000 rows: 752 → 172 ms on SQL Server, 1099 → 132 ms on Postgres). New public seam `IBatchComputedColumnProvider`.
- Nested collections two or more levels deep restrict to the parent page's rows instead of every child of the root page (about 8–13× faster on a 3-level fixture).
- Parsed table policy and computed-column config are memoized per table and invalidated on any metadata write (about 48 µs → 0.5 µs per lookup).
- `DbTable`, `ColumnDto` and `DbModel` copy a plain dictionary handed to `Metadata` in an initializer instead of wrapping it, so a later write through the caller's reference no longer reaches the object (it used to, without invalidating the memo above). Write through `.Metadata` instead.
- GraphQL gains a root field `_maxQueryRows: Int!`, the model's row ceiling (`max-query-rows`, default 10000). edit-db grouping reads it with the schema and sizes its window from it (`min(1000, ceiling - 1)` groups, plus one sentinel group) instead of sending a second aliased GROUP BY shifted by `offset: 1` to infer the ceiling, so a grouped read is one statement again.
- edit-db: nullable text fields have an explicit "Set NULL" control in the form and grid cell, so clearing a field saves `''`.

### Breaking — second review pass

- **`BigInt` is sent as a decimal string.** The scalar now writes every value as a string; input still accepts a number or a string. A JSON number past 2^53 rounds in every browser's `JSON.parse`, so a bigint key shown or sent back could name a different row. Clients that read `BigInt` fields as numbers must accept strings. `Decimal` now follows the same rule (third review pass). `@bifrostql/codegen` maps 64-bit proto integers to `string` to match.
- **SQL Server `numeric`, `money` and `smallmoney` map to `Decimal`** (were `String`). Clients sending string literals for these columns must send numbers.
- **`UseBifrostHttp3` binds `127.0.0.1` by default.** Set `BifrostHttp3Options.ListenAddress` (Host key `BifrostQL:Http3:ListenAddress`) to widen it.
- **NATS CDC publishes through JetStream** and waits for the ack. It needs a stream bound to `bifrostql.>`; without one the publish fails as transient.
- **Validation patterns** run on the linear-time regex engine, fully anchored, compiled once. Patterns with lookaround or backreferences are rejected as invalid.
- **Removed options:** LDAP `MaxOutstandingOperations` (it was never enforced); the unused logging options `EnableConsole`, `EnableFile`, `MinimumLevel`, `FilePath`.
- **Implementers:** `IWorkflowDataExecutor` gains `TransitionAsync` and cancellation tokens; `IWorkflowRunner.RunAsync` takes an optional token. `BifrostEngine`'s constructor drops its transformer and observer parameters, and `BifrostRequest.RequestServices` is required.
- **`_rawQuery` past `raw-sql-max-rows` fails** instead of returning a cut list. The field is a bare list with no place for a truncation flag.
- **`max-query-rows` below 2 is rejected at model load.** A ceiling of 1 made OData and gRPC `List` return empty pages with no continuation.
- **Refused instead of answered wrong:** relationship filters, `_agg` paths and many-to-many collections over a composite key (they joined on one column pair); `_agg` below a nested link; a query intent with two single links to the same table (the second overwrote the first); tables whose generated GraphQL names collide (`orders_aggregate` next to `orders`).
- **`DbSavedObjectStore`** creates `NVARCHAR(128)`/`VARCHAR(128)` key columns so it works on SQL Server and MySQL; ids over 128 characters are refused. Existing tables are not migrated. Saved objects are capped at 1000 per owner (507) and 512 KiB per PUT (413).
- **Continuation tokens** (gRPC, OData, S3, RESP, LDAP, feeds) issued before this release are invalid once: the identity fingerprint they bind changed.
- **`@bifrostql/react`** (experimental): the query cache key includes the endpoint; `buildGraphqlQuery` returns `{ query, variables }` and sends filters as a typed `$filter` variable; diff refuses a key change; `useBifrostSubscription` is marked unsupported.

### Security — second review pass

- **`_rawQuery` is read-only.** It runs in a transaction that is always rolled back: read-only on PostgreSQL and MySQL, `query_only` on SQLite. SQL Server also parses the text and accepts one batch with one `SELECT` and no `INTO`. The lexer follows each engine's quoting (`$tag$`, MySQL backslash escapes).
- **Four-eyes approval** compares one identity (the policy subject) on both sides; `user-audit-key` no longer changes who counts as the requester.
- **Deferred `undo`** is scoped to the caller's tenant; another tenant's change set answers not found. The approval queue shows a change only to its requester and the table's approvers.
- **Local login** is throttled per source IP and per account before the password is checked. Sessions end 8 hours after login, and role, tenant and deletion changes apply within a minute. Weak hashes are upgraded on login.
- **pgwire** cleartext password auth and **OData** Basic auth are rate-limited per source and per account (OData: `MaxBasicAuthAttemptsPerSource` / `MaxBasicAuthAttemptsPerAccount` 100, `BasicAuthAttemptWindow` 1 min, 429 over the cap). Every limiter stores account keys hashed.
- **`bifrost serve`** no longer sends any-origin CORS headers and accepts only loopback Host names.
- **`BifrostQL:DevIdentity`** refuses to start outside Development and ChatDemo.
- **gRPC** Get, List and Stream return only columns the caller may read. RESP and pgwire shape errors on a read-denied table answer exactly like the denial. `_dbSchema` hides a many-to-many bridge that names a hidden column. `@skip`/`@include` are honoured, so an excluded field is neither projected nor policy-checked.
- **`/_app-metadata`** with `RequireAuth` refuses a principal that projects to an empty identity, like `/_blob` and `/_saved-objects`. `/_blob` checks `MaxBlobBytes` before decoding base64; chat media is capped at `MaxMediaBytes` (16 MiB, 413).
- **Binary WebSocket:** grants resolve per message; the reassembly cap counts received bytes; a corrupt client chunk is not acked. Endpoint and connection faults return the same constant text as HTTP.
- **Prometheus** no longer shares scrape-cache entries between identities that differ only in a list claim. The `EngineMetrics` scrape marker can't be forged.
- **Desktop UI:** WordPress discovery errors no longer echo `wp config list` output, including `DB_PASSWORD`.
- Declared frame lengths (pgwire, LDAP, RESP) no longer pre-allocate before the bytes arrive; pgwire refuses `$N` above 32767.

### Fixed — second review pass

- **Writes:** post-commit observers and workflows fire only when a row changed, on single, batch and filtered updates. A client abort during a batch rolls back and surfaces as cancellation. In-transaction hooks and workflow runs get the request token; a workflow is cancelled on client disconnect.
- **Workflow `transition`** writes only the key and the state column, and only while the row still holds `from`; a concurrent change fails with `conflict`. Tables with computed or blind-index columns no longer fail every transition.
- **Encrypted columns:** declared validation rules run on the plaintext; the re-encryption sweep no longer overwrites a value written after it read the row.
- **Deferred changes:** timestamps read as UTC on every host time zone; the review queue works without tenants. A blank `tenant-context-key` fails fast in the outbox and deferred hooks.
- **Retention** skips and counts poison rows (`RowsFailed`, `SweepsFailed`) and drains its backlog in one pass (up to 10,000 rows). **TreeSync** honours client aborts.
- **Reads:** one link or `_agg` under two aliases returns each alias's own data; nested collections page with a primary-key tie-break; `_table` filters support `_eq: null`, share the table fields' operators and casts, and page by key.
- **SQL Server:** PIVOT over `bit` and other non-text columns; tables whose names need quoting (`report.v2`) load identity and computed flags. **SQLite:** schema PRAGMAs bind the table name.
- **Model:** links are named clear of columns; bad relationship keys are reported, not dropped; the metadata many-to-many reverse link works and two junctions between one pair both produce links. `policy-row-scope` and `ldap-base-dn` are validated with the runtime parse. Feed, LDAP, retention and chat timestamps share one date/time type list.
- **gRPC** delete reports the real affected-row count. **RESP** `RESET` re-arms the pre-auth deadline; an ambiguous bare name in `SCAN MATCH` is refused. **S3** ranged GETs fetch only the range and unranged GETs stream (`IStorageProvider.OpenReadRangeAsync`, default member).
- **Binary client:** a request over 4 MiB is refused before sending; a 1002 close no longer replays in a loop; requests past 16 per connection queue; `stream()` yields payload bytes only and acks on consume. The never-supported cross-reconnect resume is removed: after a reconnect, queries are re-sent, mutations are refused.
- **Desktop UI:** opening a SQLite file connects the server to it; Quick Start offers every schema the host serves; SQL batches keep `BEGIN…END` bodies and stop on error; bridge requests run concurrently and a timeout cancels the query on the host; vault SSH tunnels stop when their connect fails or is replaced; designer saved queries map DB names to GraphQL names, so a second schema's table is no longer queried by mistake.
- **edit-db:** grouping applies the grid's header and column filters, reads at most 1,000 groups and says when it cuts off; BigInt group keys sort numerically and BigInt columns can be summed; the "number" and "percent" formats show BigInt exactly. JSON column values past 2^53 keep their digits in the content panel and on save. The date filter takes each day boundary's own UTC offset (DST days), and date-only columns show their own day. The edit form saves NULL as NULL, and tabbing through a NULL cell stages nothing. Drill grids keep their own filters, and use the flat filter path only when the join references the parent's single-column key. Form navigation stays inside a subform's parent. Form and subform deletes ask for confirmation and show failures. An M2M pick attaches once. Composite foreign-key columns get no first-column-only dropdown. A failed FK preview shows an error, not "Record not found". Quick search keeps other URL state. The edit dialog no longer reuses a record cached with other columns.
- **edit-db CSV export** leaves a numeric literal (`-5`, `+12.50`) alone instead of prefixing an apostrophe.
- **`@bifrostql/react`:** BigInt columns sort and aggregate exactly; edits made during a save are kept; composite keys identify rows; URL page size is clamped to the configured options.
- Removed the unreachable stored-procedure and cross-database resolvers.

### Changed — second review pass

- pgwire flushes once per result and parses each statement once. LDAP resolves `member`/`memberOf` only for returned entries. The outbox drains a backlog without waiting, held events release in bounded set-based batches, and the mutation transformer chain stops at the first refusal (only its errors come back). The binary receive buffer is pooled. Chat connector tools are built once per model.

### Fixed — tables with the same name in two schemas

Several places looked a table up by its bare DB name, so a database with the same table name in two schemas crashed at load, bound to the wrong table, or could not address one of them.

- **Model load:** WordPress and Drupal detection, and the synthetic foreign keys they add, no longer throw "An item with the same key has already been added" for two sites in two schemas. Each schema's tables get their own metadata and links.
- **GraphQL names:** an emitted table name always wins over a `schema_name` alias of another table, and an alias two tables share resolves to neither. The schema that gets no alias is `schema-prefix-default`, not a hard-coded `dbo`.
- **Lookup-table enums:** enums are keyed by schema and table, so a same-named lookup table in a second schema no longer fails the endpoint or rewrites the other table's filters. A bare `enum-ref` resolves in the column's own schema first; if two other schemas both define it, schema build fails and asks you to qualify it as `schema.table`.
- **Name-based relationships:** a parent inferred from a column name is looked up in the child's schema first, and is not inferred when two other schemas both define it.
- **Nested links:** a link two levels below a role-named link (`orders { billing_address { countries } }`) resolves from the parent's table instead of failing with "A requested field does not name a queryable table". A link or `_agg` nested under a collection now reads its key from its own parent row; before, a parent past the first could get another parent's related record.
- **MCP:** `describe_table`, table resources and every data tool accept `schema.name`. A bare name two readable tables share is refused and the prompt lists the qualified names.
- **Feeds:** the route and a scoped token's allow-list entries resolve to a table the same way, and the scope check compares the resolved table, not the route text. A bare name two schemas share is a not-found.
- **Desktop UI:** Copy DDL and Edit-as-query send `schema.name`. The DDL bridge resolves DB names only; a GraphQL name is now a not-found.
- `EnumTableConfig.MetadataKey` moved to `MetadataKeys.Enum.Table` (the key is still `enum`). `IQueryField.ToSqlData` takes the parent's `IDbTable?` instead of the parent field.

### Fixed — every front door reports a read the row ceiling cut, instead of truncating it

`max-query-rows` clamps every LIMIT inside SQL generation. Adapters that paged or capped a read set `Limit = page + 1` themselves and compared the row count with their own page, so they never saw that clamp: under a ceiling at or below the page, the database returned the ceiling, the count never passed the page, and a cut answer read as complete. Each surface now takes its LIMIT, page and "more" test from one shared window that applies the same clamp the SQL does, and reports a cut in its own envelope. The page is at most one below the ceiling, so the probe row still fits; a surface with a fixed cap therefore refuses one row earlier than before at the defaults.

- **pgwire:** a `SELECT` with no `LIMIT` now returns up to the ceiling instead of 100 rows. A result the ceiling cuts fails with SQLSTATE `54000` and `result exceeds the server row limit; add a LIMIT clause`, on both the simple and extended protocols.
- **OData:** under a ceiling below `$top`, the page narrows and still carries a `@odata.nextLink`. `$top=0` no longer emits a nextLink that resumed at the same offset forever; `$top=0&$count=true` still counts. A `$expand` collection the ceiling cut is refused with the existing fan-out 400.
- **gRPC:** `List` mints a page token from a probe row instead of "the page came back full", so a ceiling no longer ends paging early or hands out a token to an empty page. A `Stream` cut by its page size or the ceiling sets the response trailer `bifrost-truncated: true`; a complete stream carries none.
- **RESP:** `MGET` and `EXISTS` with more keys for one table than the default window no longer answer nil / uncounted for the keys past row 100. A batch over the ceiling is refused: `ERR too many keys for one table: <n> requested, at most <ceiling> per command`. `SCAN` pages under a ceiling below `COUNT` instead of returning cursor `0` after the first page; a ceiling of 1 is refused.
- **LDAP:** under a ceiling below `SearchBatchSize`, a search no longer ends each family after one short batch (unpaged: partial result with `success`; paged: the empty last-page cookie). A ceiling of 1 is refused with `adminLimitExceeded`. `SearchBatchSize` now means rows consumed per round trip; each fetch reads one more.
- **S3:** a listing over the scan cap is refused instead of reporting `IsTruncated=false` on the first 10,000 keys. The effective cap is `min(MaxListMaterialize, max-query-rows - 1)`.
- **Prometheus:** grouped aggregates carried no LIMIT and were cut to 100 groups before the cardinality check, so a metric over its bound was emitted partially and never marked capped. The collector now probes one past the bound and caps the metric; the effective cap is `min(cap, max-query-rows - 1)`.
- **GraphQL `_agg`:** the per-row aggregate statement read the parent table under its filter but not its window, so `orders(limit: 100) { data { _agg(...) } }` grouped every matching order. It now reads the same paged window as the root select (filter, sort, offset, limit). Root aggregates gain the window's ORDER BY.
- **MCP:** `aggregate` and declarative query tools take their truncation flag from the same window.

### Fixed — SQLite BOOLEAN and BIT columns read as booleans

A SQLite column declared `BOOLEAN` or `BIT` stores 0/1 and the driver returns it as an integer. The schema advertises it as `Boolean`, and the stock scalar rejected the integer, so every read of such a column failed with `Unable to convert '1' value of type 'Int64' to the scalar type 'Boolean'`. The `Boolean` scalar now maps 0 and 1 to `false` and `true`. Any other integer still fails the field rather than being coerced.

### Changed — the binary transport copies less per frame

- A binary response used to be copied about four times on its way to the socket, and a received
  payload twice. `BifrostMessage.ToBytes` now sizes the frame first and writes into one array,
  wrapping the payload without copying it. `FromBytes` keeps the array the parser already made.
  Result JSON is serialized through a pooled buffer instead of a growing `MemoryStream`. Measured
  allocation per `ToBytes` of a 1,000-row result: 1,549 KB to 262 KB.
- The server now writes the payload as the last field of a frame. A payload of 16 KiB or more goes
  out as a second fragment of the same WebSocket message, after the other fields, so it is never
  copied into a frame-sized array. Chunked results are cut straight from those two parts instead of
  from a serialized copy of the whole result. Protobuf readers accept fields in any order, and both
  shipped decoders (.NET `BifrostMessage.FromBytes`, `@bifrostql/binary-client`) already did; a
  custom client that reads one `ReceiveAsync` as one message must loop until `EndOfMessage`.
- Transport bench allocation per end-to-end 10,000-row query (process-wide, client included): 62 MB
  to 30 MB (HTTP allocates 23 MB); 1,000 rows: 3.4 MB to 3.0 MB.

### Changed — edit-db formats grid cells from a per-column formatter

- edit-db built a new `Intl.NumberFormat` for every number and percent cell, called
  `Date.toLocaleString` for every `DateTime` grid cell, and re-resolved each column's display
  format and enum labels per cell. Each column now gets one compiled formatter (`columnFormatter`),
  cached on the column object, and the grid's cells call it. Output is unchanged. Measured median
  per cell over a 1,000-cell page (`EDITDB_PERF=1` timing test, jsdom): number 273 → 7.2 µs,
  percent 603 → 9.3 µs, datetime column 65 → 28 µs, `DateTime` grid cell 26 → 12.6 µs.

### Changed — the binary transport runs requests concurrently

- One `/bifrost-ws` connection served one request at a time: a slow query held up every request
  behind it, and a request sent while a chunked response was in transit was refused with "A
  chunked response is in transit". The connection now has one reader, which routes each
  `ChunkAck`/`ChunkNack` to the transfer its `request_id` names, and one serialized writer, so
  requests run concurrently and their frames interleave. A connection runs at most
  `BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection` (16) requests; one past that, or one
  reusing the `request_id` of a request still in flight, is refused with an `Error` frame. Each
  message now runs in its own DI scope instead of the upgrade request's, so a scoped service (a
  grant resolver and the `DbContext` it reads) is no longer shared across a connection's requests.
  A client `Close` lets requests still executing send their answers before the server closes.
- Breaking: `ChunkSender.SendChunkedAsync` is removed. `ChunkSender.StartTransfer` returns a
  `ChunkTransfer`, whose `RunAsync` sends the chunks and waits for the acks handed to it through
  `Acknowledge`; it never reads the socket.

### Fixed — unique violations recognized by driver code

- A database error whose text quoted a uniqueness phrase was reported as `CONFLICT`. Drivers quote
  the caller's value in other errors (PostgreSQL 22P02, MySQL 1366, SQL Server 2628), so a value
  reading "duplicate key" turned an invalid-value or truncation error into a conflict, and the
  operation-id claim treated any such error as a lost claim. `BifrostExecutionError.IsUniqueViolation`
  now reads the driver's code: PostgreSQL `23505`, MySQL error 1062, SQL Server 2627/2601, SQLite
  extended code 2067/1555. An exception that is not a `DbException` is never a unique violation.
- A workflow step was reported as `conflict` when its error text contained BifrostQL's own conflict
  message, so a value quoting that message failed its step as a conflict. The runner now reads the
  `CONFLICT` error code. `IBifrostWorkflowExecutor` errors keep their joined message and now carry
  the GraphQL error (the conflict, when there is one) as `InnerException`.

### Breaking — dialect date/JSON hooks take a lowering callback

A `computed-expr` column read through a relationship now qualifies its columns with the joined
table's alias; before, a many-to-many read whose junction shared a column name (such as `id`)
failed with an ambiguous-column error. To carry the alias, `ISqlDialect.LowerExpression` gains an
optional `tableAlias` argument, and the `SqlDialectBase` hooks `LowerDateAdd`, `LowerDateDiff`,
`LowerDatePart` and `LowerJsonGet` now take `(node, Func<SqlExpr, string> lowerOperand)` instead of
`(node, IDbTable, SqlParameterCollection)`.

**Migration:** in a third-party dialect, replace `LowerExpression(node.X, table, parameters)` with
`lowerOperand(node.X)` inside those four overrides.

### Breaking — `admin` no longer bypasses policy unless the model names it

A caller whose roles included `admin` bypassed every policy grant, row scope, column deny and
state-machine role check, and read encrypted columns in plaintext, whatever the model declared:
the role was hard-coded at every seam, and an identity provider issues that word for its own
administrators. No role bypasses policy now unless the model names one:

```
:root { policy-admin-role: admin }
```

The load stamps the value onto every table, so every seam answers from it; a table declaring a
different value fails model load. See [the admin role](/BifrostQL/guides/authorization/#the-admin-role).

**Migration:** a model that relied on `admin` bypassing policy adds
`:root { policy-admin-role: admin }`, or names the role it actually means. Code that used
`MetadataKeys.Policy.DefaultAdminRole` (removed), passed an `adminRole` to `PolicyEvaluator` or
a policy transformer or provider (parameter removed), or called `PolicyEvaluator.IsAdmin(identity)`
or `HoldsAnyGrant(identity, grants)` (now take the table policy or the admin role) recompiles
against the new signatures.

### Breaking — `transition-not-by` needs a `populate: created-by` author column

The author column named by `transition-not-by` used to be any column, so a client could write
someone else's name into it and then approve its own row. The column must now declare
`populate: created-by`, or model load fails. The actor and the author now compare case-blind, and
two uuids compare as values.

**Migration:** add `populate: created-by` to the column `transition-not-by` names.

### Breaking — an insert that leaves out a defaulted tenant foreign key is refused

The tenant foreign-key check treated a foreign-key column left out of an insert as `NULL`, but the
database writes the column's default, which could reference another tenant's row. An insert that
leaves out a defaulted or computed foreign-key column is now refused with `ACCESS_DENIED`, unless
another column of the same key is `NULL`.

**Migration:** send the foreign-key value explicitly.

### Changed — a fault in operation code is `OPERATION_FAILED`

An ordinary exception thrown by a mutation operation's own code (a `FormatException` from bad
input) used to answer `DATABASE_ERROR`. It now answers `OPERATION_FAILED` with a constant message.

**Migration:** a client that matched `DATABASE_ERROR` for these failures must match
`OPERATION_FAILED`.

### Changed — chat sends the API key as `Authorization: Bearer`

The chat module now uses the Anthropic C# SDK 13, which speaks Claude API version `2026-10-21` and
sends the API key in an `Authorization: Bearer` header instead of `X-Api-Key`. `BifrostQL.Core`
depends on `Anthropic` 13.x.

**Migration:** a gateway or proxy behind `Chat:BaseUrl` that reads `X-Api-Key` must read
`Authorization`. A host that references `Anthropic` 12 directly must move to 13.

### Breaking — engine-only history APIs

`HistoryErasure.Marker` is now internal; there is no public replacement, because erasure runs only
from the retention engine. `HistoryErasure.BuildTrailPurgeSql` now takes the resolved history
`IDbTable` and the `entity` / `entity_id` values. `ApprovalDecisionService`, `DeferredUndoEngine`
and `DeferredReviewQueue` gain an optional `endpoint` constructor argument, and `IBifrostContext`
gains `Endpoint`.

**Migration:** recompile code that constructs those services, and pass the new arguments to
`BuildTrailPurgeSql`.

### Changed — PostgreSQL constraint violations name their kind and constraint

An exclusion-constraint violation used to answer `DATABASE_ERROR`; it now answers
`EXCLUSION_VIOLATION`. A foreign-key violation now answers `FOREIGN_KEY_VIOLATION` instead of
`DATABASE_ERROR`. A unique violation still answers `CONFLICT`. All three now carry the constraint's
name in `extensions.constraint`. See [Constraint violations](/BifrostQL/guides/mutations/#constraint-violations).

**Migration:** a client that matched `DATABASE_ERROR` for an exclusion or foreign-key violation
must match the new codes.

### Changed — a mutation hook is called only for tables its `AppliesTo` accepts

The per-row write path called every `IBeforeCommitMutationHook` and `IInTransactionMutationHook`
for every table, whatever its `AppliesTo` said, while the bulk-batch and filtered-update paths
skipped it. Every path now calls a hook only for a table its `AppliesTo` accepts.

**Migration:** a hook whose `AppliesTo` refuses a table its body still acts on must widen
`AppliesTo` to those tables. A hook that does not override `AppliesTo` is unaffected.

### Changed — SQL Server binds `DateTime` as `datetime2`

A `DateTime` was bound as `datetime`, which rounds to 1/300 s, so an `_eq` filter or key on a
`datetime2` column matched no row and writes lost precision. Values now bind as `datetime2`; a
comparison against a `datetime` or `smalldatetime` column casts the parameter to that type.

### Added — operation ids record a write under the caller's id

A table with `operation-id: optional` records an `insert`, `update`, `upsert` or `delete` that
carries `_operationId` or an `Idempotency-Key` header. The record holds the caller's tenant and
user id, the id, the table, the verb, the target key, an input hash and the result, in the hidden store named
by `:root { operation-id-store: <table> }`, written on the write's own transaction. A refused
write leaves no record, and a write without an id runs as before. Model load refuses a store
without a primary key or unique index over exactly `(tenant, caller, operation_id)`. See
[Operation ids](/BifrostQL/guides/mutations/#operation-ids).

A retry with the same id and the same request answers with the recorded result, generated key
included, and runs no SQL write, transformer or hook. The retry still passes the table's policy
for its verb, checked against the column names the policy declares, so a caller who lost the grant gets `ACCESS_DENIED`; an `upsert` retry must pass
both the create and the update check.

`_batch` and `save` take one id for the whole batch or saved tree: the actions or nodes and the
record commit in one transaction, so a failed batch leaves no record, and a retry answers the
recorded result without a second write. A batch with an id runs row by row, never on the bulk
path. A `save` whose tree reaches an `approval` table is refused with `OPERATION_ID_UNSUPPORTED`.

Two requests with the same id at the same time commit one effect: the second waits for the first
to commit and answers as a retry would (the recorded result, or `OPERATION_ID_REUSED` for a
different request) instead of a `CONFLICT` on the store.

A known id sent with a different table, verb, target key or arguments is refused with
`OPERATION_ID_REUSED` and writes nothing, so a client can tell a reused id from a `CONFLICT` on
its own data.

A table with `operation-id: required` records a write that carries an id and refuses every write
without one with `OPERATION_ID_REQUIRED`, writing nothing. The check runs on every write path, so
`sync`, `delta`, `updateWhere`, file, custom-operation, chat plan and protocol adapter writes
cannot write the table. Model load refuses `required` beside `retain`,
`ttl`, `deferrable`, an `encrypt` column or a chat table, whose engines write without an id, and
refuses `operation-id` on a hidden table.

Operation ids need a caller with a user id. A write that carries an id from a caller without one
is refused with `OPERATION_ID_ANONYMOUS` and writes nothing: such callers would share one id
space, so one could be answered with another's recorded result.

The store keeps every record unless it declares a `ttl` (`ttl: 30d after created_at`). With one,
the retention purge deletes older records, and a retry of a purged id runs as a fresh write.

### Fixed — racing operation ids on MySQL no longer answer `DATABASE_ERROR`

Three or more requests racing one operation id whose first attempt rolled back could deadlock on
MySQL (error 1213), and the request the database rolled back answered `DATABASE_ERROR`. It now runs
once more and answers from the record the surviving request commits.
The victim is recognized by its driver code (SqlState `40001` or `40P01`, SQL Server error 1205),
never by its text, so an unrelated error that mentions a deadlock is not retried.

### Changed — gRPC, RESP, MCP and the binary transport name operation-id refusals

A gRPC write refused for its operation id answered `INTERNAL`, which clients retry. A write to an
`operation-id: required` table now gets `FAILED_PRECONDITION` with the message
`OPERATION_ID_REQUIRED: this write requires an operation id.` `OPERATION_ID_REUSED` maps to
`ALREADY_EXISTS`; `OPERATION_ID_UNSUPPORTED` and `OPERATION_ID_ANONYMOUS` to `FAILED_PRECONDITION`.
A client that branches on `INTERNAL` for these writes should branch on the new status.

RESP answered the same refusal with `ERR internal error` and MCP with `execution_error`. RESP now
replies `-OPERATION_ID_REQUIRED this write requires an operation id.`, with the code as the error
kind, and MCP returns `OPERATION_ID_REQUIRED: this write requires an operation id.` The other
`OPERATION_ID_*` codes are named the same way on both.

The binary WebSocket transport (`/bifrost-ws`) sent the bare GraphQL message,
`This write requires an operation id.`, without the code the HTTP wire carries in
`extensions.code`. It now sends `OPERATION_ID_REQUIRED: this write requires an operation id.`, and
the other `OPERATION_ID_*` codes the same way. Every other binary error keeps its GraphQL message.

### Changed — a hidden table's invalid retention fails model load

A `visibility: hidden` table's `retain` or `ttl` is now checked at model load like an exposed
table's. Before, an invalid one loaded and the purge skipped the table with an error on every
pass. A model with such a table now fails to load: fix the duration or anchor column.

### Fixed — retention, deferred review, empty inserts and auth limiters

- A retention erasure of a history-tracked row failed on PostgreSQL when the trail used the
  documented `JSONB` `entity_id` column: the trail purge compared it to a text parameter and
  PostgreSQL has no `jsonb = text` operator, so the purge rolled back and no tracked row could
  expire. The purge now compares each trail column in its own type.

- Retention deleted a row that changed between the expiry read and the delete: a session
  refreshed in that window was still expired, and a soft-deleted row restored in it was still
  hard-purged. Each delete now matches only while the anchor holds the value the read saw.

- A server operation whose writes touched two or more `undo-window` tables opened one deferred
  change set per write, so a reviewer could approve or undo half of an all-or-nothing operation.
  Every write in an operation now shares one change set.

- An insert that supplies no columns (`insert: {}`, valid since default-aware inserts made every
  column of such a table optional) rendered `INSERT INTO t() VALUES()` and failed with
  `DATABASE_ERROR` on SQL Server, PostgreSQL and SQLite; a `_batch` insert of `{}` was silently
  skipped. Both now insert a row from the columns' defaults (`DEFAULT VALUES`, or `() VALUES()` on
  MySQL). A batch holding such an insert uses the per-row path. `ISqlDialect` gains
  `EmptyInsertValues`, with a default implementation.

- A table whose `history-writer` names no registered writer refused each write with a message that
  held the schema-qualified table and every registered writer name. The client now gets a constant
  message ("The table's history writer is not registered, so the change was refused."), and the
  detail goes to the server log at Debug level through `BifrostErrorSink`.

- On a host with more than one endpoint, `approve`, `undo` and the deferred review queue's reject
  failed with "Multiple BifrostQL endpoints are registered", because their replay intent named no
  endpoint; the change stayed pending. The replay also named its table by bare `DbName`, which is
  ambiguous when another schema holds the same name. Each endpoint's inputs now carry its own path,
  and the replay names that endpoint and the schema-qualified table. `ApprovalDecisionService`,
  `DeferredUndoEngine` and `DeferredReviewQueue` gain an optional `endpoint` constructor argument
  (source-compatible; recompile callers), and `IBifrostContext` gains `Endpoint`, which defaults to
  `null`.

- The pgwire, RESP and LDAP auth-attempt limiters stopped counting new keys once their tracking map
  held 20,000 live counters, so a peer that first filled the map from many addresses could then
  guess a fresh account's password without limit. A source or account the full map cannot track now
  counts against one shared window for its axis, under the same cap. During such a flood, new
  callers share that budget and may be refused; already-tracked sources and accounts are unaffected.

## 0.4.28 — 2026-11-09

Tag `v0.4.28` · NuGet `0.4.740` · npm `@bifrostql/react` and `@bifrostql/types` `0.4.0`

### Breaking — recompile server operations

`BifrostOperationContext.QueryAsync` gained an optional `sort` parameter. Source still compiles, but
the binary signature changed.

**Migration:** rebuild any operation assembly. One compiled against an earlier BifrostQL fails with
`MissingMethodException`.

### Breaking — a database error ends a mutation operation

A database error inside a mutation operation now ends it on every dialect, even if its code catches
the error, and so does a write the policy or the validator refuses. The transaction rolls back, the
client gets an error with the first failure's code, and no observer fires. On PostgreSQL a caught read failure used
to return success while COMMIT silently rolled the writes back. A refusal that comes before any
statement runs may still be caught, such as `LOCK_ROW_NOT_FOUND`, a read of a denied column, or a
write to an approval-gated table.

**Migration:** check a value before the statement that would fail on it, rather than catching the
failure. See [One transaction per mutation operation](/BifrostQL/guides/server-operations/#one-transaction-per-mutation-operation).

### Breaking — `@bifrostql/react`: `useBifrostBatch` and coded errors

`useBifrostBatch` writes through the server's `<table>_batch` field, one transaction per document, so
a failure rolls back that whole document. `allowPartialSuccess` is gone. `BatchResult`, `BatchError`
and `BatchProgress` changed shape, a batch over `batch-max-size` is refused whole, and the table's
`batch-duplicate-policy` now applies. A coded GraphQL error is judged by its `extensions.code`, not
its message, so only `UNAUTHENTICATED` and `UNAUTHORIZED` start the session flow.

**Migration:** see "Breaking changes" in `packages/@bifrostql/react/CHANGELOG.md`, which covers each
shape. A server operation that relied on the word "unauthorized" must set
`ErrorCode = "UNAUTHENTICATED"`.

### Changed — the batch fast path keeps document order

The fast path now merges only adjacent rows with the same verb, columns and filter, so
`[insert x, delete y, insert y]` gives the per-row result where it used to fail with `CONFLICT`.
A batch that changes verb or column set on every row loses the speedup: past 8 runs it takes the
per-row loop. See [How the fast path keeps document order](/BifrostQL/reference/bulk-batch-performance/#how-the-fast-path-keeps-document-order),
which also names where the two paths still differ.

### Fixed — PostgreSQL `uuid` keys work as text

Generated updates, deletes and upserts, and `UpdateAsync`, `DeleteAsync` and `LockRowAsync`, failed
with `uuid = text` on a key sent as a string, which is the only way GraphQL can send one. Only a value
that binds as text is cast; a number binds bare, so `1.5` on an integer key matches no row on a delete
or a read, and answers `LOCK_ROW_NOT_FOUND` on a lock. An update refuses it first, with `INVALID_VALUE`.
A double or decimal on a `real` key is narrowed, and past `real`'s precision it reaches another row:
16777217 finds the row holding 16777216. A decimal or double on an integer key scans the whole table.
A long, double or decimal on `money`, an int on a `boolean` and a bool on an integer column fail with
`DATABASE_ERROR`. A text key
on an enum or `bit` column still fails, and a `citext` key is compared as text, so a lookup in another
case finds no row. See [Type mapping specifics](/BifrostQL/reference/dialects/#type-mapping-specifics).

Two spellings PostgreSQL reads as one uuid are now one key in a batch, on both paths, and a resumed
deferred undo finds a uuid-keyed row. A SQL Server `uniqueidentifier` is treated the same way. SQLite
keeps a column declared `UUID` as text and MySQL has no uuid type, so each spelling stays its own key
there.

### Changed — a value its column cannot read is `INVALID_VALUE`

A malformed uuid, a bad date or a number out of range used to answer `DATABASE_ERROR`. It now
answers `INVALID_VALUE` with a generic message, from the SQLSTATE. An out-of-range key sent as text on a
PostgreSQL integer column fails this way where it used to match no row. The validator gives the same
code for a value it refuses because of the column's type: unreadable text, a fraction or an out-of-range
number on an integer column, too many digits before the decimal point, a date outside the type's
storable range, or a binary value longer than the column.

**Migration:** code that matched `DATABASE_ERROR` for these conditions must match `INVALID_VALUE`.
See [Type mapping specifics](/BifrostQL/reference/dialects/#type-mapping-specifics).

### Fixed — a PostgreSQL login that does not own the schema now sees its keys

Keys came from `information_schema.constraint_column_usage`, which only shows the table owner, so
a least-privilege login built a model with no keys and its keyed writes changed nothing or failed.
They now come from `pg_constraint`. See
[Reading keys on PostgreSQL](/BifrostQL/reference/dialects/#reading-keys-on-postgresql).

### Breaking — `_fileUpload.accessUrl` removed; file metadata no longer stores a presigned URL

Uploading a file used to compute a presigned access URL and store it in the column's
pointer JSON, where it was also copied into history rows, CDC messages and audit
exports. The stored value was a bearer credential that outlived its own 15-minute
expiry and every access check that would have refused to mint it again — and for the
local provider it was a server filesystem path.

- `FileMetadata.AccessUrl` is no longer written or read, and the `accessUrl` field is
  gone from `FileUploadResult` in the schema.
- Rows that already carry a persisted `AccessUrl` still deserialize; the key is simply
  ignored.

**Migration:** select the file's key from the upload result and mint a URL per read
with `_fileDownload`.

### Changed — `_fileDownload` expiry is clamped by the bucket

`expirationMinutes` used to reach the storage provider unclamped, so a caller could ask
for the provider's own ceiling (seven days on SigV4) or crash the resolver with an
out-of-range value. Each bucket now declares `maxurlexpiry`
(`maxPresignedUrlExpirationMinutes`, default 60 minutes): a caller's value may only
narrow the window, a non-positive value is rejected, and `expiresAt` reports the
clamped expiry.

### Breaking — `_join` / `_single` containers and the `dynamic-joins` switch removed

Every row type advertised `_join` and `_single` container fields (types `<T>_join` /
`<T>_single`, one `<table>(on: [String!])` member per table) that the query builder
could never execute — selecting one failed with "A requested field does not name a
queryable table." An advertised field that cannot execute is a defect, so the
containers are removed rather than implemented:

- `_join` / `_single` and the `<T>_join` / `<T>_single` types no longer appear in the
  SDL.
- The model-level `dynamic-joins` metadata key is retired. It is now an unrecognized
  `:root` key, and model load fails with the standard unknown-key error until it is
  removed.
- The explicit `_join_<table>` path accepts only `_eq` / `_neq` as the `on` operator;
  every other operator, a multi-column `on`, or a malformed `on` value is refused with
  an identifier-free error.

**Migration:** delete `dynamic-joins` from `:root` metadata and navigate through the
relationship fields BifrostQL derives from foreign keys and declared `join` metadata
(see [Joins](/BifrostQL/guides/joins/)).

### Breaking — `_hardDelete` now requires the `soft-delete-hard-role` opt-in

`_hardDelete` used to be generated on **every** soft-delete table, so any caller could
physically delete rows — including already-soft-deleted ones — with no role metadata
anywhere. Hard delete now defaults **OFF**:

- The schema emits `_hardDelete` only on tables that declare
  `soft-delete-hard-role: <role>`, and the caller must hold that role.
- Without the opt-in the argument is absent from the SDL and the hard-delete branch is
  unreachable; a programmatic mutation intent carrying the key is denied.

**Migration:** add `soft-delete-hard-role: <role>` to the table's metadata (and grant
that role to the callers that purge), e.g.
`"dbo.orders { soft-delete: deleted_at; soft-delete-hard-role: purge_admin; }"`.
See [Modules — Soft delete](/BifrostQL/guides/modules/#hard-delete-is-off-by-default).

### Fixed — scoped-away upsert no longer returns the victim's key

An upsert whose primary key lives outside the caller's row scope (another tenant, or a
soft-deleted row) ran the update branch, matched zero rows, and still returned the
row's key — and the key-vs-insert difference exposed whether a cross-tenant key was
taken. A zero-row upsert update now answers the update path's not-found response (`0`),
never the key; batch upserts count only rows actually affected.
