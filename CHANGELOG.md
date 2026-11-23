# Changelog

All notable changes to BifrostQL after `3c42a60` (`[DART-xDCKBXmI5qsv] add app-builder extraction plan from Membership Manager build`).

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Versions

Each section is headed by the git tag that shipped it and names its NuGet build. NuGet
packages are versioned `0.4.<CI run number>`, so a NuGet number does not match its tag: tag
`v0.4.28` is NuGet `0.4.740`. Every `BifrostQL.*` package is published at the same version on
each release, so keep them on one version in a host. When a tag's publish failed, its changes
first reached NuGet with the next tag, and the section is headed by that tag.

| Tag | Date | NuGet build |
|-----|------|-------------|
| v0.4.31 | 2026-11-22 | 0.4.837 |
| v0.4.30 | 2026-11-22 | 0.4.831 |
| v0.4.29 | 2026-11-22 | 0.4.819 |
| v0.4.28 | 2026-11-09 | 0.4.740 |
| v0.4.27 | 2026-11-02 | 0.4.731 |
| v0.4.26 | 2026-11-02 | 0.4.729 |
| v0.4.25 | 2026-11-01 | 0.4.727 (v0.4.24's publish failed) |
| v0.4.23 | 2026-10-19 | 0.4.711 |
| v0.4.22 | 2026-10-17 | 0.4.709 (v0.4.21's publish failed) |
| v0.4.20 | 2026-09-21 | 0.4.688 |
| v0.4.19 | 2026-09-17 | 0.4.685 (v0.4.18's publish failed) |
| v0.4.17 | 2026-09-02 | 0.4.597 |
| v0.4.16 | 2026-08-31 | 0.4.590 |
| v0.4.15 | 2026-08-24 | 0.4.583 |
| v0.4.14 | 2026-08-24 | 0.4.581 |
| v0.4.13 | 2026-07-07 | 0.4.494 |
| v0.4.12 | 2026-07-05 | 0.4.484 |

The npm packages `@bifrostql/react` and `@bifrostql/types` are released on their own; see
`packages/@bifrostql/react/CHANGELOG.md`. Version 0.4.0 of both shipped with v0.4.28.

To upgrade across several builds, read every section from your build up to the target and
apply each breaking or behaviour-change step on the way.

## Unreleased

On `main` after v0.4.31; not yet on NuGet.

## 0.4.31 — 2026-11-22 (async auth and filter operators)

Tag `v0.4.31` · NuGet `0.4.837` · npm `@bifrostql/react` and `@bifrostql/types` unchanged at `0.4.1`

### Security — async auth and identity projection

- **An `IPolicyGateAccessor` resolved from the root service provider (captured by a singleton) throws** instead of serving one caller's gate to every caller, in MS DI and in containers that cache a root-resolved scoped service, such as Autofac.
- **A grant resolver that throws any exception after the request is cancelled (for example `SqlException`) refuses the caller** instead of degrading to empty permissions; it surfaces as `OperationCanceledException`, so MCP handles it as a cancellation rather than an unhandled driver exception.

### Breaking — async auth and identity projection

- **No thread blocks on the grant resolver or the policy gate's model load.** `IPolicyGate` is no longer injectable: inject `IPolicyGateAccessor` and `await GetAsync(ct)`; the model loads only for requests that use the gate, so health checks and static routes never trigger a schema load. `HttpContext.GetBifrostUserContext()` is now `GetBifrostUserContextAsync()`. While an `IGrantResolver` is registered, the synchronous `IBifrostAuthContextFactory.CreateUserContext` refuses an authenticated principal instead of blocking; await `CreateUserContextAsync` instead, as every shipped transport does.
- **`MapBifrostGrpc()` is replaced by `await app.MapBifrostGrpcAsync()`,** which awaits the model load before mapping instead of blocking a thread at startup. A host that mapped inside a synchronous `Configure` callback moves to minimal hosting.
- **`PrometheusScrapeScopeResolver.ResolveScope` is now `ResolveScopeAsync`** and awaits the host auth factory's async projection.
- **An explicit `--config` path that names no file is refused** (`Config file not found: <path>`) by `serve`, `doctor`, `watch`, `schema`, `export`, `test` and `config-generate`; it used to be ignored silently.

### Fixed — async auth and identity projection

- **`bifrost config-validate` refuses every config file `serve` refuses** (for example `"Path": 5`, a non-boolean `DisableAuth`, a non-string connection string); both read through one function.
- **A host auth factory returning keys that differ only in case no longer fails the whole Prometheus scrape;** the affected row-scoped metrics are excluded and the detail is logged.
- **A host `IBifrostAuthContextFactory` decorator or a third-party protocol adapter can run the grant resolver:** await the new `IBifrostAuthContextFactory.CreateUserContextAsync`, which every shipped gate awaits.
- **A host auth factory that throws for the Prometheus service identity excludes only the row-scoped metrics** instead of faulting the whole scrape.
- **gRPC retries the contract build after a failed model load** instead of rethrowing the first fault for the life of the process.
- **Concurrent `IPolicyGateAccessor.GetAsync` calls in one request share one build,** and one caller's cancellation no longer faults the others.
- **`bifrost serve --connection-string … --config f.json` refuses the same wrong-shaped config files `config-validate` refuses.**
- **A host auth factory's internal timeout no longer faults the whole Prometheus scrape;** only the row-scoped metric is excluded.
- **A grant-resolver projection cut short by the caller's cancellation is refused** instead of served with empty permissions.
- **`bifrost doctor` refuses every config file `serve` and `config-validate` refuse,** checks `connectionStrings` even when `--connection-string` is given, and reports a file with no `connectionStrings.bifrost` with serve's message. It used to report "All checks passed" for a file serve then refused.
- **`bifrost watch`, `schema`, `export`, `test` and `config-generate` read `--config`.** They accepted the flag and ignored it; they now take `connectionStrings.bifrost` from the file when `--connection-string` is absent and refuse a file serve refuses.
- **`bifrost schema` and `export` build the model with the config file's metadata rules** through serve's own model build, so `schema --config` prints the schema `serve` would serve; a rule the database cannot satisfy is refused with exit 1 and the validator's message, pointing to `bifrost config-validate`. `bifrost config-generate` reads the database without the file's rules, so it bootstraps a fresh database and still detects tables the file hides.
- **`IPolicyGateAccessor.GetAsync(userContext, ct)` builds the gate from a context the endpoint already projected,** so the pre-flight gate and the write answer for one identity and the grant resolver runs once per request.
- **`bifrost schema` and `export` point at `config-validate` only when metadata validation fails;** other build failures, such as a provider with no registered factory, no longer get that hint.
- **Binary WebSocket transport: concurrent requests on one connection no longer race on the shared upgrade `HttpContext`.** On a mount with no auth middleware the first read of `User` installed a feature concurrently, which could fail a request with "Malformed or unprocessable frame" and close the connection. Each message now gets its own `HttpContext`, a copy of the upgrade request taken when the connection opens; that copy is what `IHttpContextAccessor`, auth-context factories and hooks see. `Items` written during one message no longer carry over to the next.
- **Model-load metadata validation throws the new `ModelConfigValidationException`,** which derives from `InvalidOperationException`, so existing catches still work.

### Fixed — filters

- **A column filter takes several operators at once.** `date: { _gte: "2026-11-16", _lte: "2026-11-22" }` failed with "unknown single-link relationship 'date'" wherever it appeared: at the top of a filter, beside another column, inside `or`, or inside a relationship sub-filter. Each operator is now its own predicate on the column, and they AND together.

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

### Security

- A caller whose roles included `admin` bypassed every policy grant, row scope, column deny and
  state-machine role check, and saw encrypted columns in plaintext, whatever the model said:
  the role was hard-coded at every seam. An identity provider issues `admin` for its own
  administrators, so any such login was a BifrostQL superuser. Constructor arguments on the
  policy transformers changed only the two seams that read them, and the documented escape hatch
  (registering them with a role nobody holds) left the rest open. No role bypasses policy now
  unless the model names one in `:root { policy-admin-role: <role> }`, which the load stamps onto
  every table so every seam reads the one declaration; a table declaring a different value fails
  model load. The state-transition audit row passes policy as the engine, through an in-process
  marker no request can supply. Breaking: `MetadataKeys.Policy.DefaultAdminRole` is removed;
  `PolicyEvaluator`, the policy and state-machine transformers and the row-capability and
  transitions providers lose their `adminRole` constructor parameter; `PolicyEvaluator.IsAdmin`
  takes the table policy (or a role) and `HoldsAnyGrant` the admin role;
  `StateMachineRoleGate.CanUseTransition` takes the admin role. A model that relied on `admin`
  adds `:root { policy-admin-role: admin }`.

### Added

- Operation ids. A table with `operation-id: optional` records an `insert`, `update`, `upsert`
  or `delete` that carries `_operationId` or an `Idempotency-Key` header: tenant, caller, id,
  table, verb, target key, input hash and result go into the hidden store named by
  `:root { operation-id-store: <table> }`, on the write's own transaction. A refused write
  leaves no record; a write without an id runs as before. An id on `sync`, `delta` or
  `updateWhere` is refused with `OPERATION_ID_UNSUPPORTED`. Model load checks the
  store exists, is `visibility: hidden`, carries every column and has a primary key or unique
  index over exactly `(tenant, caller, operation_id)`. See the mutations guide.
- Operation-id replay. A retry with the same id and the same request answers with the
  recorded result, generated key included, and runs no SQL write, transformer or hook. The
  retry still passes the table's policy for its verb, checked against the column names the
  policy declares, so a caller who lost the grant gets `ACCESS_DENIED`; an `upsert` retry must
  pass both the create and the update check.
- Operation ids on `_batch` and `save`. One id covers the whole batch or saved tree: the
  actions or nodes and the record commit in one transaction, so a failed batch leaves no record,
  and a retry answers the recorded result without a second write. A batch with an id runs row
  by row, never on the bulk path. A `save` whose tree reaches an `approval` table is refused with
  `OPERATION_ID_UNSUPPORTED`.
- Concurrent operation ids. Two requests with the same id at the same time commit one effect:
  the second waits for the first to commit and answers as a retry would (the recorded result,
  or `OPERATION_ID_REUSED` for a different request) instead of a `CONFLICT` on the store.
- `OPERATION_ID_REUSED`. A known id sent with a different table, verb, target key or arguments
  is refused with this code and writes nothing, so a client can tell a reused id from a
  `CONFLICT` on its own data.
- `operation-id: required`. A table with this mode records a write that carries an id and
  refuses every write without one with `OPERATION_ID_REQUIRED`, writing nothing. The check runs
  on every write path, so `sync`, `delta`, `updateWhere`, file, custom-operation, chat plan and
  protocol adapter writes cannot write the table. Model load
  refuses `required` beside `retain`, `ttl`, `deferrable`, an `encrypt` column or a chat table,
  whose engines write without an id, and refuses `operation-id` on a hidden table.
- Operation ids need a caller with a user id. A write that carries an id from a caller without
  one is refused with `OPERATION_ID_ANONYMOUS` and writes nothing: such callers would share one
  id space, so one could be answered with another's recorded result.
- Operation-id store retention. A `ttl` on the store (`ttl: 30d after created_at`) bounds it:
  the retention purge deletes older records, and a retry of a purged id runs as a fresh write.
  Without a `ttl` the store keeps every record.

### Changed

- `HistoryErasure.BuildTrailPurgeSql` now takes the history table and the `entity` /
  `entity_id` values, so each comparison can follow the trail column's type. Callers outside
  BifrostQL pass the resolved history `IDbTable` and the two values they bind.
- The chat module uses the Anthropic C# SDK 13, which speaks Claude API version `2026-10-21`
  and sends the API key as `Authorization: Bearer` instead of `X-Api-Key`. A `Chat:BaseUrl`
  gateway or proxy that reads `X-Api-Key` must read `Authorization` instead. `BifrostQL.Core`
  now depends on `Anthropic` 13.x (Apache-2.0; 12.x was MIT), so a host that references
  `Anthropic` 12 directly must move to 13.

### Fixed

- On MySQL, three or more requests racing one operation id whose first attempt rolled back could
  answer one of them with `DATABASE_ERROR`: the two waiting claims deadlocked (error 1213). The
  claim the database rolls back now runs once more and answers from the recorded result.
  The victim is recognized by its driver code (SqlState `40001`/`40P01`, SQL Server error 1205),
  never by its text, so an unrelated error that mentions a deadlock is not retried.
- gRPC answered a write refused for its operation id with `INTERNAL`, a status clients retry.
  A write to an `operation-id: required` table now gets `FAILED_PRECONDITION` with
  `OPERATION_ID_REQUIRED: this write requires an operation id.`; `OPERATION_ID_REUSED` maps to
  `ALREADY_EXISTS`. The text is fixed per code and names no table.
- RESP and MCP answered the same refusal with `ERR internal error` and `execution_error`. RESP
  now replies `-OPERATION_ID_REQUIRED this write requires an operation id.` and MCP
  `OPERATION_ID_REQUIRED: this write requires an operation id.`; the other `OPERATION_ID_*`
  codes are named the same way.
- The binary WebSocket transport (`/bifrost-ws`) answered the same refusal with the bare GraphQL
  message, `This write requires an operation id.`, dropping the code the HTTP wire carries in
  `extensions.code`. It now sends `OPERATION_ID_REQUIRED: this write requires an operation id.`,
  and the other `OPERATION_ID_*` codes the same way; every other error keeps its GraphQL message.
- A `visibility: hidden` table's `retain` or `ttl` was not checked at model load, although the
  purge sweeps hidden tables. An invalid one (bad duration, missing or non-timestamp anchor)
  loaded and the purge skipped the table with an error on every pass. Model load now refuses it,
  as it does for an exposed table.
- A `computed-expr` column read through a relationship wrote its column references unqualified,
  so a many-to-many read whose junction shares a column name with the target (a surrogate `id`)
  failed with an ambiguous-column error, and any join could bind the name to the wrong table.
  Expression columns now qualify every column with the joined table's alias, as plain and
  `computed` columns already did. Breaking for third-party dialects: `ISqlDialect.LowerExpression`
  gains an optional `tableAlias`, and the `SqlDialectBase` hooks `LowerDateAdd`, `LowerDateDiff`,
  `LowerDatePart` and `LowerJsonGet` now take `(node, Func<SqlExpr, string> lowerOperand)` instead
  of `(node, IDbTable, SqlParameterCollection)`. Lower each operand with `lowerOperand(node.X)`.

- On PostgreSQL a write refused by an exclusion constraint (SQLSTATE `23P01`) or a foreign key
  (`23503`) answered `DATABASE_ERROR`, and one refused by a unique constraint (`23505`) answered
  `CONFLICT`, none of them naming the constraint, so a client could not say which rule the write
  broke. Exclusion violations now answer `EXCLUSION_VIOLATION` and foreign-key violations
  `FOREIGN_KEY_VIOLATION`, each with a constant message, and all three carry the constraint's name
  in `extensions.constraint` (`BifrostExecutionError.ConstraintName`). The statement and the values
  stay off the wire. SQL Server, SQLite and MySQL report no such SQLSTATE and are unchanged.
- A before-commit or in-transaction mutation hook was called for every table's writes on the per-row
  path, whatever its `AppliesTo` said, while the bulk-batch and filtered-update paths skipped it for
  the same table. The composites now call a hook only for a table its `AppliesTo` accepts, so both
  paths agree and a hook no longer needs to repeat the table check in its body. A hook that
  overrides `AppliesTo` to refuse a table its body still acts on stops running for that table;
  widen its `AppliesTo` to the tables it acts on.
- A retention erasure of a history-tracked row failed on PostgreSQL when the trail used the
  documented `JSONB` `entity_id` column: the trail purge compared it to a text parameter and
  PostgreSQL has no `jsonb = text` operator, so the purge rolled back and no tracked row could
  expire. The purge now compares each trail column in its own type.

- Retention deleted a row that changed between the expiry read and the delete: a session
  refreshed in that window was still expired, and a soft-deleted row restored in it was still
  hard-purged. Each delete now matches only while the anchor holds the value the read saw.
- SQL Server: a `DateTime` value was bound as `datetime`, which SqlClient rounds to 1/300 s.
  Since compatibility level 130 that rounded value no longer equals a `datetime2` column's, so an
  `_eq` filter or a key that includes a `datetime2` column matched no row, and a write stored the
  value rounded (`.1234567` became `.1233333`). Values now bind as `datetime2`. A comparison
  against a `datetime` or `smalldatetime` column casts the parameter to that type, so it still
  matches the value a client spells.
- A server operation whose writes touched two or more `undo-window` tables opened one deferred
  change set per write, so a reviewer could approve or undo half of an all-or-nothing operation.
  Every write in an operation now shares one change set.
- `HistoryErasure.Marker` was a public static field, so any `IMutationIntentExecutor` caller could
  place it in an intent's `UserContext` and have an authorized delete erase the row's whole
  change-history trail. It is now internal, like the other engine-only capabilities. Breaking for
  out-of-tree code that read it; there is no public replacement by design — erasure runs only from
  the retention engine.
- The tenant foreign-key check treated a foreign-key column left out of an insert as `NULL`. When
  the column has a database default, the database writes that default, so `account_id DEFAULT 2`
  let a tenant-1 insert reference tenant 2's account. An insert that leaves out a defaulted or
  computed foreign-key column is now refused with `ACCESS_DENIED` unless another column of the same
  key is `NULL`; send the value explicitly.
- An insert that supplies no columns (`insert: {}`, valid since default-aware inserts made every
  column of such a table optional) rendered `INSERT INTO t() VALUES()` and failed with
  `DATABASE_ERROR` on SQL Server, PostgreSQL and SQLite; a `_batch` insert of `{}` was silently
  skipped. Both now insert a row from the columns' defaults (`DEFAULT VALUES`, or `() VALUES()` on
  MySQL). A batch holding such an insert uses the per-row path. `ISqlDialect` gains
  `EmptyInsertValues`, with a default implementation.
- A mutation operation whose own code threw an ordinary exception (a `FormatException` from bad
  input) was reported as `DATABASE_ERROR`. It now fails with `OPERATION_FAILED`
  (`BifrostOperationContext.OperationFailedCode`) and a constant message, with the exception kept
  as the inner exception. A statement that failed earlier in the operation still names the error.
- A table whose `history-writer` names no registered writer refused each write with a message that
  held the schema-qualified table and every registered writer name. The client now gets a constant
  message ("The table's history writer is not registered, so the change was refused."), and the
  detail goes to the server log at Debug level through `BifrostErrorSink`.
- `transition-not-by` accepted an author column the client could write, so an author could store
  someone else's name and then approve their own row. It also compared the actor and the author
  case-sensitively, so a uuid read back in lowercase did not match an uppercase IdP subject. The
  named column must now declare `populate: created-by`, or model load fails (breaking for a model
  that named any other column). The comparison ignores case, and two uuids compare as values.
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

## 0.4.28 — 2026-11-09 (least-privilege logins and operations)

Tag `v0.4.28` · NuGet `0.4.740` · npm `@bifrostql/react` and `@bifrostql/types` `0.4.0`

### Added

- `BifrostOperationContext.LockRowAsync(table, key)` locks a row until the operation's
  transaction ends, so concurrent operations that read to decide what to write run one after
  another. It stays inside the caller's `tenant-filter` tenant, so a row of another tenant raises
  the same `LOCK_ROW_NOT_FOUND` as a missing one. On MySQL and SQL Server the attempt waits while
  that tenant holds the row, so the delay can show the row exists. On MySQL a caught
  `LOCK_ROW_NOT_FOUND` also leaves locks that hold up other writers and inserts until the operation
  ends. Read policy does not apply and the lock grants nothing. A dialect with no row lock (SQLite)
  is refused with `ROW_LOCK_UNSUPPORTED` rather than running unserialized.
- `BifrostOperationContext.QueryAsync` takes `sort` (`<column>_asc` / `<column>_desc`, as a
  generated field does), and `where` can test `BifrostOperationContext.NotNull` or
  `BifrostOperationContext.LessThan(value)`. A `null` value is now documented as IS NULL. See "Reading rows" and "Serializing on a row" in the Server Operations
  guide.
- `@bifrostql/react` rejects with `BifrostGraphQLError`, which keeps the server's
  `extensions.code` as `code` and the whole list as `errors`, so a client can tell a conflict from
  a denial without matching message text. `BatchError` gains `code`.

### Changed

- **Recompile operations.** `BifrostOperationContext.QueryAsync` gained an optional `sort`
  parameter. That keeps source compatible, but the method's binary signature changed, so an
  operation assembly compiled against an earlier BifrostQL fails with `MissingMethodException`
  until it is rebuilt.
- A database error inside a mutation operation now ends the operation on every dialect, even if the
  operation catches it, and so does a write the policy or the validator refuses. The operation's
  transaction rolls back, the client gets an error carrying the first failure's code, and no observer fires. On
  PostgreSQL a caught read failure used to return success while COMMIT silently rolled the writes
  back. A refusal that comes before any statement runs does not end it and may still be caught:
  `LOCK_ROW_NOT_FOUND`, `PARTIAL_PRIMARY_KEY`, a read of a denied or unknown column, `LessThan(null)`,
  and a write to an approval-gated or unknown table.
- A value the validator refuses because of its column's type now has the code `INVALID_VALUE`: text
  it cannot read as a number, date or time (`"2.4"` for an integer), a typed decimal with a fraction
  on an integer column, a number outside the integer type's range, too many digits before the decimal
  point, a date outside the type's storable range, and a binary value longer than the column. A
  refusal that also breaks a declared rule keeps no code, as before.
- The set-based batch fast path merges only adjacent rows with the same verb, columns and filter,
  and applies each run in document order. It used to group a verb's rows wherever they sat in the
  document, so `[insert x, delete y, insert y]` ran both inserts first and failed with `CONFLICT`
  when the later insert reused a key the delete freed. Batches of one verb and one column set stay
  one statement. A batch of more than 8 runs now takes the per-row loop, which includes a batch that
  alternates verbs or an optional column on every row: those were set-based, in the wrong order, and
  10,000 of them now take about as long as the per-row path. See "How the fast path keeps document
  order" in the bulk batch performance reference, which also names where the two paths still differ.

- `@bifrostql/react` decides whether a GraphQL error is an auth failure by its `extensions.code`
  when it has one: only `UNAUTHENTICATED` and `UNAUTHORIZED` take the session flow. A coded refusal
  whose message says "unauthorized" used to log the user out. An operation that relied on that
  wording must set `ErrorCode = "UNAUTHENTICATED"`. An error with no code is read as before.

- A database error for a value its column cannot read (SQLSTATE `22P02`, `22007`, `22008`, `22003`)
  now has the code `INVALID_VALUE` instead of `DATABASE_ERROR`, with a generic message. Code that
  matched `DATABASE_ERROR` for these needs the new word. An out-of-range key sent as text on a
  PostgreSQL integer column now fails this way where it used to match no row.

### Fixed

- On PostgreSQL, a `uuid` key sent as text failed with `uuid = text` on generated updates, deletes
  and upserts, and on `UpdateAsync`, `DeleteAsync` and `LockRowAsync`. GraphQL can only send a uuid
  as a string, so this blocked every uuid-keyed table. A comparison now casts a value that binds as
  text to the column's type. Any other value binds bare and compares by the database's own rules, so
  `1.5` on an integer key matches no row on a delete or a read, and answers `LOCK_ROW_NOT_FOUND` on a
  lock. On an update the validator refuses it first with `INVALID_VALUE`, which ends a mutation
  operation. The text `"2.4"` fails with `INVALID_VALUE` on all four. The one exception to binding
  bare is a double or decimal on a `real` column, which is narrowed so it can find the value the
  column reads back. A value past `real`'s precision narrows onto another row: 16777217 finds the row
  holding 16777216, and an update or delete changes that row. A decimal or double on an integer key
  compares as that type, so PostgreSQL scans the whole table rather than using the key's index. A
  long, double or decimal on `money`, an int on a `boolean` and a bool on an integer column have no
  comparison and fail with `DATABASE_ERROR`. The cast covers the types `PostgresDialect.NeedsParameterCast` lists: a
  text key on an enum or `bit` column still fails with `operator does not exist`, and a `citext` key
  stays bare, so a lookup in another case finds no row. Batches with uuid text also lost the fast
  path without saying so, because binary COPY refuses a string for a uuid column; canonical text now
  stays on it.
- Two spellings PostgreSQL reads as one uuid (case, hyphens, braces) are now one key in a batch, on
  both paths. Compared as text they were two rows, so the fast path let the database pick a winner,
  the count read 2, and `batch-duplicate-policy: reject` never fired. A SQL Server `uniqueidentifier`
  is treated the same way, and that includes a hyphenless spelling SQL Server itself refuses, so a
  batch can collapse it before the database would have said so. SQLite keeps a column declared `UUID`
  as text and MySQL has no uuid type, so each spelling stays its own key there. A whitespace-wrapped
  uuid now fails with `INVALID_VALUE` on both paths; the fast path used to write it.
- A deferred undo on a PostgreSQL uuid-keyed table that was interrupted after its inverse committed
  failed with `uuid = text` on every retry and never left `undoing`.
- A PostgreSQL login that does not own the schema saw no primary, foreign or unique keys, because
  `information_schema.constraint_column_usage` only shows the owner. Updates through such a login
  found no key and changed nothing or failed with "has no primary key columns". Keys now come from
  `pg_constraint`, and composite keys keep their declared order. For an owner, the one visible
  change is that same-named constraints on different tables no longer pick up each other's columns.

## 0.4.27 — 2026-11-02 (history writer plugins)

Tag `v0.4.27` · NuGet `0.4.731`

### Added

- History writer plugins. `history-writer` (model or table level) picks the `IHistoryWriter`
  that stores each change `HistoryMutationHook` records. The default, `table`, is the
  existing `history-table` writer, now `HistoryTableWriter`. A host registers its own writer
  as an `IHistoryWriter` singleton to write an application-owned audit table in the change's
  transaction. Before, an app with an audit table of its own could not use the history
  module at all, because the target had to match the fixed `history-table` contract. A table
  on a plugin writer needs no `history-table` and gets no `<table>History` read field. See
  "Writing to your own audit table" in the Change History guide.

## 0.4.26 — 2026-11-02 (packaging)

Tag `v0.4.26` · NuGet `0.4.729`

### Added

- `BifrostQL.Tool` (the `bifrost` CLI) is published to nuget.org with every release, so
  `dotnet tool install -g BifrostQL.Tool` works. It was packable but never pushed.

### Fixed

- CLI examples in the README and docs used `--connection`; the tool only accepts
  `--connection-string`.
- The desktop app guide no longer tells users to `dotnet tool install -g BifrostQL.UI`; the
  desktop app is not a dotnet tool. It now documents building from source and the Windows
  installer script.

## 0.4.25 — 2026-11-01 (server-secured operations, approval hardening)

Tag `v0.4.25` · NuGet `0.4.727`. Made in `v0.4.24`, whose publish failed.

### Added

- Server operations: `IBifrostOperation` + `AddOperation<T>()` add application-defined root
  query and mutation fields to `/graphql`. Exposure and grants come from
  `:root { operations: name[grant,list] }`, the tenant rule from `operations-tenant`. Data access
  through `BifrostOperationContext` always acts as the caller, and a mutation operation's reads
  and writes share one transaction. See the Server Operations guide.
- `transition-not-by: from->to:column` on a state machine refuses a transition to the caller
  whose audit actor equals the row's author column (the "approver is not the author" rule).
- `UseBifrostQL(BifrostAuthGateScope.BifrostRoutes)` gates only the GraphQL endpoint and
  playground. The parameterless `UseBifrostQL()` still gates every route after it.

### Fixed

- GraphQL errors carry `BifrostExecutionError.ErrorCode` as `extensions.code` (`ACCESS_DENIED`,
  `PENDING_APPROVAL`, `CONFLICT`, …) instead of always `BIFROST_EXECUTION_ERROR`.
- Any `storage`/file metadata no longer breaks the schema: the `Upload` scalar is defined (a
  base64 string in JSON requests; multipart is still unsupported).
- With Bifrost auth enabled, a host's own default scheme (for example
  `AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`) is kept in either registration
  order; previously a valid bearer token got 401.
- An update drops client-supplied `created-on`/`created-by` values.
- Docs, README and the sample host config no longer use the never-supported `update: none` key.

### Security

Maker-checker approval (`approval: enabled`). See the repository's security advisory.

- `approve` and `reject` decide a pending change only for a caller in the change's tenant; a
  change in another tenant answers exactly like an id that does not exist.
- `pending_changes` is read-only to every API (GraphQL, batch, sync, filtered update, file
  mutations, protocol adapters, MCP) once a table is approval-gated. `reject` and `ExpireAsync`
  record their own transition, so a store declared `policy-actions: read` keeps working.
- Every read of `pending_changes` is scoped to the caller's tenant (a caller with no tenant claim
  sees only changes queued without one).
- An approved replay stamps the requester, not the approver, into `created-by`/`updated-by`/
  `deleted-by` and the change-history actor. The approver is recorded on `pending_changes.approver`.

Upgrading: follow the advisory's guidance on reviewing existing `pending_changes` rows.

## 0.4.23 — 2026-10-19 (retention covers hidden tables and every endpoint)

Tag `v0.4.23` · NuGet `0.4.711`

### Fixed

- The retention purge now sweeps `visibility: hidden` tables. `DbModel` dropped
  hidden tables from `Tables`, and the purge only walked `Tables`, so a hidden
  table's `ttl`/`retain` never ran: `public.sessions { visibility: hidden;
  ttl: 1d after expires_at }` kept expired sessions forever. Hidden tables now
  sit in `IDbModel.HiddenTables`. Query and mutation intents reach them only
  when they carry `HiddenTableAccess`, which only Core can mint, so hidden
  tables stay off GraphQL, REST, MCP and every protocol adapter.
- The purge covers every endpoint, not just the first one. It ran
  `PurgeOnceAsync` on the first registered endpoint with no endpoint path,
  which the intent executors refuse once two endpoints exist. Each pass now
  sweeps each physical table (connection data source + database + schema +
  table) at most once across all endpoints (`PurgeEndpointsOnceAsync`). When
  endpoints that declare retention for a table disagree on windows, anchors,
  tenant column, soft-delete column or hard-delete role, the table is skipped
  and an error logged (fail-closed). Neither window wins. Purge deletes now
  name the table schema-qualified.

## 0.4.22 — 2026-10-17 (write-path transactions, mutation input requiredness)

Tag `v0.4.22` · NuGet `0.4.709`. Made in `v0.4.21`, whose publish failed.

### Added

- `MutationTransformContext` carries the write's `Connection`, `Transaction`,
  `Dialect` and `CancellationToken`. Every transformer chain on a write path
  (single-row insert/update/delete/upsert, per-row batch, `updateWhere`,
  tree-sync) now runs inside the write's transaction — insert, delete and
  `updateWhere` previously ran their chain before the transaction opened — so
  a host guard reads state that commits with the write instead of opening its
  own connection. `Transaction` is null on tree-sync (SQL-level `BEGIN`), and
  the bulk set-based batch fast path still transforms before its transaction
  exists (`Connection` null).
- `MutationTransformContext.LoadRowForUpdateAsync(table, columns, keyColumns)`
  reads one row in the write's transaction under the dialect's update lock.
- `IMutationTransformer.RequiresWriteTransaction(table)` (default `false`): a
  transformer that reads through the connection declares it per table, and
  that table stays off the bulk batch fast path.

### Changed

- Update input types are partial: only the primary key is required. An UPDATE
  sets only the columns supplied, so `orders(update: { id: 3, status: "paid" })`
  now passes validation on a table with other NOT NULL columns (it previously
  failed with "field … of required type … was not provided").
- Insert input requiredness honours column defaults: a field is required only
  when the column is NOT NULL, has no default, and is neither identity nor
  computed. Schema readers now report `ColumnDto.HasDefault` on all four
  dialects (SQL Server `COLUMN_DEFAULT` or `rowversion`; Postgres
  `column_default` or identity; MySQL `COLUMN_DEFAULT`, `auto_increment` or
  `DEFAULT_GENERATED`; SQLite `dflt_value`). Upsert keeps its key required and
  applies the insert rule to the other fields.
- Computed / generated columns are now excluded from write input types on all
  four dialects. Previously only SQLite reported `IsComputed` (7286a09c); SQL
  Server (`COLUMNPROPERTY … 'IsComputed'`), Postgres (`is_generated = 'ALWAYS'`)
  and MySQL (`generation_expression`) now do too, so those columns also leave
  the insert/update/upsert inputs and are read-only in `_dbSchema`.
- Computed columns are never write inputs. A host must not rely on a computed
  column being a required (or present) input as a write-deny mechanism; use
  `policy-write-deny`, which the policy transformer enforces by refusing
  write-denied keys on every write path.
- An update that supplies only the primary key is now refused with
  `NO_UPDATE_COLUMNS` ("An update must set at least one non-key column.")
  instead of returning `0` with no statement run. Partial update inputs made
  `update: { id: 3 }` pass validation, so a caller that forgot its fields saw
  the same answer as "row not found". Applies to single-row update, `_batch`
  (per-row and bulk paths, the whole batch rolls back) and
  `IMutationIntentExecutor`; decided on the caller's own fields, not on
  columns a transformer stamps. A key-only upsert of an existing row stays a
  no-op returning `0`. **Behaviour change** for callers that relied on the
  silent `0`.

### Fixed

- State-machine transitions are race-free. The current-state read now takes
  the dialect's update lock, and the `UPDATE` re-checks the state it was
  validated from (`state = <loaded state>`); if the row moved between read and
  write the update fails with `CONFLICT` and rolls back instead of silently
  applying a transition nobody validated. Behaviour change: a concurrent
  transition that used to "win" by overwriting now surfaces `CONFLICT` (code
  `CONFLICT`, the existing concurrency-conflict message).
- Security: a write can no longer point a foreign key at another tenant's
  row. `TenantMutationTransformer` pinned a row's own tenant column but never
  checked what it referenced, so a tenant-1 caller could insert or update a
  row whose FK named tenant 2's parent. The new auto-registered
  `TenantForeignKeyMutationTransformer` checks, inside the write transaction
  and under the dialect's update lock, that every database FK whose parent
  declares `tenant-filter` names an existing parent in the caller's tenant
  (all columns of a composite FK; NULL FKs and untenanted shared parents are
  skipped; no admin bypass). Covers single-row insert/update/upsert, batch,
  tree-sync and `updateWhere`; tables with such an FK leave the bulk batch
  fast path. Refusal is `ACCESS_DENIED` with one constant message, "A
  referenced row is not available.", for a missing and a foreign parent alike.
  **Behaviour change:** cross-tenant FK writes that used to succeed are now
  refused, and an update that sets only part of a composite tenant-scoped FK
  is refused. Existing rows are not re-validated.
- A transformer that throws a non-`BifrostExecutionError` exception on insert,
  delete or `updateWhere` now surfaces as the sanitized `DATABASE_ERROR`, as it
  already did on update, because the chain runs inside the transaction.
- Build: `BifrostQL.SqlServer` pins `System.Security.Cryptography.Pkcs` per
  TFM (8.0.2 / 9.0.21 / 10.0.13) and moves to `Microsoft.Data.SqlClient`
  7.0.2; the Pkcs versions SqlClient pulls fall inside CVE-2026-84008 and
  CVE-2026-70162 and failed the NuGet audit. The `dotnet new bifrost`
  template carries the same SqlClient and Pkcs pins, so a scaffolded SQL
  Server project restores clean.
- Model load kept column length, precision and scale. The per-build column
  clone in `DbModelLoader.BuildModel` (every `LoadAsync`, every per-profile
  build) dropped `CharacterMaxLength`, `NumericPrecision` and `NumericScale`,
  so in production schema-derived validation never refused an over-length
  string or an out-of-precision decimal, and `_dbSchema` advertised no length.
  The clone and `ColumnDto.WithGraphQlName` now share one structural copy, so a
  column property added later is carried by both.
- The implied `required` form/meta hint (`ValidationRules.RequiredImplied`,
  surfaced as `required` in `_dbSchema` validation rules) no longer marks a NOT
  NULL column that has a database default; it now agrees with the insert input
  type. Both read one predicate, `ColumnDto.RequiresInsertValue` (NOT NULL, no
  default, not identity, not computed).
- The SQL Server schema reader reports an `IDENTITY` column as having a default, as the
  PostgreSQL and MySQL readers already did (`INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` is
  `NULL` for identity). Insert requiredness was already right through `IsIdentity`; only
  `ColumnDto.HasDefault` was wrong. Added in `v0.4.22`.

## 0.4.20 — 2026-09-21 (edit-db host options)

Tag `v0.4.20` · NuGet `0.4.688`. Reconstructed from the commit history.

### Added

- edit-db: the host chooses which column a grid's quick search starts on, and the sort each
  grid opens with.

### Fixed

- edit-db: select menus open beneath their trigger.

## 0.4.19 — 2026-09-17 (authorization epic)

Tag `v0.4.19` · NuGet `0.4.685`. Made in `v0.4.18`, whose publish failed.

### Added

- `bifrost-codegen --grants <response.json>` (alongside `--proto-file` or `--endpoint`) emits the policy grant catalogue as a typed `Grant` union and `GRANTS` constant in `grants.ts` beside the message files, re-exported from the generated barrel so `@bifrostql/types` consumers get it.

- Packages: `usePolicy` in `@bifrostql/react` and `@bifrostql/app-shell` reads the server's `_grants` and `_dbSchema` projection, cached per session identity; `ProtectedRoute` gates on `requiredGrants` and `FieldControl` on the column's `writable` flag.
- Packages: `BifrostTable` and `useBifrostTable` derive editing from the caller's `_dbSchema` projection. `editable` defaults to `'auto'` (edits iff `allowedActions` includes `update` and a write handler is wired; explicit `true` still requires `onRowUpdate`, `false` wins), a column takes an editor only when projected `writable`, a `readable: false` column renders `—` rather than empty, and a row's `_can { update delete }` overrides the table-level answer for inline editing and for row actions that declare a `permission`. The projection is fetched once per `identity` and shared with `usePolicy`; a projection failure leaves the table read-only with the error surfaced. New hook surface: `policy`, `rowCan`, `isColumnMasked`, `identity`.
- Scoped `IPolicyGate` for custom endpoints to reuse GraphQL table and column policy decisions.
- Per-row `_can { update delete }` capabilities for row-scoped and self-deny tables.
- `policy-row-scope-exempt` grants for tenant-filtered row-scope bypass.
- Column-level `writable-values` literal allow-lists for mutation policy.
- `policy-self-deny` to prevent callers, including administrators, changing
  protected columns on their own row.
- HostedSpa sample: the membership-manager guards (closed baseline, role-backed
  grants, officer-only deletion, finance-only money writes, masked reads, own-row
  scope with officer exemption, constrained status, self-protected role) are
  metadata lines in `appsettings.json`, with `AddBifrostGrantResolver` resolving
  roles to permissions (the seeded admin to every permission the catalogue names,
  so `_grants` matches what the admin bypass already allows); the sidecar workflow
  endpoints ask the scoped `IPolicyGate`
  before their first write. The authorization guide's guards-to-metadata table
  quotes the sample's lines and `HostedSpaPolicyTests` holds the two in step.

### Fixed

- `writable-values`: a supplied value that cannot be coerced to the column type
  is refused with the generic column-write `ACCESS_DENIED` message instead of the
  tenant-context wording, and a non-coercible literal now fails model load
  naming the key, column and literal.
- History before/after images now apply tracked-table read policies, preventing
  denied and masked values from bypassing authorization in the trail.
- Read-deny now takes precedence over `read-requires`; schema projections retain
  masked columns and omit refused columns, and contradictory metadata fails load.
- Approved mutations keep permission-only policy grants when they replay.

### Removed

- The unused `schema-permissions` metadata key; it was parsed but never enforced, creating a silent fail-open permission declaration.

### Changed — `_dbSchema` is caller-projected; raw metadata is admin-only (BREAKING, S8)

- `_dbSchema` now resolves **per caller**: the model is projected through the same policy evaluator every other catalog uses, so a table or column the caller may not read is absent, and each table carries `allowedActions: [String!]!` (subset of `read, create, update, delete` the caller may perform) while each column carries `readable: Boolean!` / `writable: Boolean!` (a masked column is `readable: false` yet stays selectable — the value is nulled). The raw `metadata` bag — which contains the `policy-*` rules themselves — is now served to **admin callers only**; non-admin consumers that read table or column `metadata` must migrate to the typed fields. `isEditable` is retained and means exactly "the table has a key". New root fields: `_grants: [String!]!` (the caller's roles+permissions union) and `_policyGrants: [String!]!` (every grant name referenced in the model's policy metadata, sorted and de-duplicated — the catalogue a profile editor lists). `bifrost-codegen` is unaffected: it reads the `.proto` type schema, not `_dbSchema` data.

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

### Fixed — typed tenant and row-scope claims (E8)

- Tenant and policy row-scope claims are now coerced to the target column type before binding predicates or tenant insert pins. String and one-element array claims such as `"42"` bind as `bigint` 42; invalid or multi-element values fail closed with `ACCESS_DENIED` instead of reaching PostgreSQL as text.

### Fixed — write path resolves schema-qualified table names (M11-w)

- `MutationIntent.Table` / `MutationBatchIntent.Table` (the `IMutationIntentExecutor` adapter write seam) and the `table` argument of `_fileUpload` / `_fileDelete` / `_fileDownload` now accept a schema-qualified `schema.name` and resolve it exactly; a bare name still resolves when it is unique across schemas. Previously every client-supplied name went through a bare-`DbName` lookup, so a table existing in two schemas (`sales.orders` + `archive.orders`) was unwritable through these surfaces — the write was refused fail-closed. An ambiguous or unknown name produces the same sanitized error (`BifrostErrorSink.LookupMiss`), byte-identical and carrying no table name. The resolution rule is the new `IDbModel.TryGetTableFromClientName`; the five keyed-write seams (`TableMutationPipeline`, `BatchMutationPipeline`, `BulkBatchPlanBuilder`, `FilteredUpdatePipeline`, `FilePointerAccess`) are unchanged — they receive the resolved `IDbTable`.

### Fixed

- PostgreSQL array-column insert and update parameters now cast known array types (such as `text[]` and `integer[]`) instead of binding string literals bare. Apps that relied on this failure as an accidental guard (Track did) must gate the column (S3/S4a) before upgrading.

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

## 0.4.17 — 2026-09-02

Tag `v0.4.17` · NuGet `0.4.597`. Reconstructed from the commit history.

### Security

- Blind-index columns are hidden from every read surface and are always server-derived; a
  client can no longer write one.

### Fixed

- The `tenant-filter` column is optional in mutation input types; the server fills it.
- `@bifrostql/react`: table-name `invalidateQueries` entries invalidate, the three write hooks
  share one invalidation helper, built queries use BifrostQL's paged `data { }` envelope, and
  the library build externalizes `react/jsx-runtime`.

## 0.4.16 — 2026-08-31

Tag `v0.4.16` · NuGet `0.4.590`. Reconstructed from the commit history.

### Added

- Query row limits are clamped to the model's `max-query-rows` ceiling.
- RESP refuses credentials on a cleartext transport, and gRPC takes its admission slot before
  the TLS handshake.
- `@bifrostql/react`: `fields` and `sort` are constrained to the row's keys (`FieldNameOf`).
- edit-db `saveFile` extension point, so a host intercepts every editor download. The desktop
  app adds Open in ERD and Copy DDL.

### Fixed

- A batch delete's `WHERE` uses only the predicate columns.
- Wire context can no longer fill keys that the security modules resolve.

### Removed

- The obsolete two-argument `SqlExecutionManager` constructor.

## 0.4.15 — 2026-08-24

Tag `v0.4.15` · NuGet `0.4.583`

### Fixed

- `BifrostQL.Abstractions` is packed and published with each release. This is its first
  NuGet build.

## 0.4.14 — 2026-08-24 (multi-model mutation surface)

Tag `v0.4.14` · NuGet `0.4.581`

### Added — multi-model mutation surface (generous save recipes)

- **`delta:`** collection-diff save on every table's mutation field: `{ inserted, updated, deleted }` flattens onto the batch pipeline in that order — one transaction, `batch-max-size`, `batch-duplicate-policy`, and the set-based bulk fast path unchanged. Returns the total affected count.
- **`save:`** explicit-ops graph save: every node of a nested `<t>_save` document states its own `_op` (`bifrost_save_op` enum), defaults are key-present-updates / key-absent-inserts, unlisted children are untouched (no orphan inference — that stays `sync:`'s reconcile semantic), root delete is legal, and no current-state load is needed. Executes on the TreeSync executor: per-node transformer chain, instance-scoped FK flow, one transaction.
- **`updateWhere:`** filtered set-update (`UPDATE ... SET ... WHERE`), **opt-in** via `filtered-update: enabled` table metadata with a `filtered-update-max-affected` cap (default 100, COUNT-prechecked in-transaction with rollback). The caller's `where` reuses the read-side filter grammar, ANDs into transformer row scope, and its columns clear the same column-permission guards as reads. Tables with hooks, state machines, or concurrency tokens refuse the set-based form.
- `TableFilter.CombineAnd` public combinator (converges three private copies).

## 0.4.13 — 2026-07-07 (hardening)

Tag `v0.4.13` · NuGet `0.4.494`. Reconstructed from the commit history; no entries were
written for this release.

### Security

- GraphQL depth and complexity limits on every endpoint, including the binary WebSocket
  transport. Maximum depth is 20 even when unconfigured; set `BifrostQL:MaxQueryDepth` and
  `BifrostQL:MaxQueryComplexity` (single database) or `BindQueryLimits(section)` (multiple
  databases).
- Policy results are no longer memoized across callers, which leaked one caller's row scope to
  another.
- The binary WebSocket transport enforces profile authorization, and profile filtering of
  security modules fails closed.
- Raw SQL validation checks the exact SQL that runs and closes comment and quote bypasses.
- Desktop app: CORS, SSH and vault credentials are no longer exposed on loopback.

### Fixed

- Aggregate correlation, pagination, filter and join SQL; a relationship filter with several
  predicates renders as one ANDed subquery.
- Cancellation reaches mutation writes and SQL, and a composite-key update returns its key.
- A null foreign key resolves to null or empty, and table lookups ignore case.
- Binary client and edit-db fixes: reconnects, the shared ack window, BigInt keys and
  URL-driven filters.

## 0.4.12 — 2026-07-05 (package split, validation on by default)

Tag `v0.4.12` · NuGet `0.4.484`. Reconstructed from the commit history; no entries were
written for this release.

### Breaking

- SQL Server support moved from `BifrostQL.Core` to `BifrostQL.SqlServer`: the dialect, type
  mapper, schema reader, `DbAuthConnectionWrapper` and the SQL Server-backed metadata sources.
  A SQL Server host must reference `BifrostQL.SqlServer`. Core's default type mapper is now
  `AnsiSqlTypeMapper`.
- The S3 storage provider moved from Core to `BifrostQL.Aws`, registered with
  `AwsStorageRegistration.Register()`. `BifrostQL.Aws` is not published to NuGet.
- Core references only the GraphQL engine. `BifrostQL.Server` carries the GraphQL server
  bundle and GraphiQL, so code that got ASP.NET Core through Core must reference it directly.
- Server-side validation runs on every insert and update. Declared rules (pattern, min/max,
  length) used to need `server-validation: enabled`; set `server-validation: off` on a table
  or column to opt out there.

### Security

- Tenant isolation is enforced on the mutation path, and aggregate join chains are scoped by
  the tenant and soft-delete filters.
- Raw database error messages no longer reach clients.
- OIDC tokens from unmapped issuers and unknown `policy-actions` tokens are rejected instead of
  failing open.
- The desktop app binds `127.0.0.1` by default; `--expose` opts into the LAN.

### Fixed

- Single-row mutations are transactional.
- Malformed metadata (numeric, boolean, enum, filter operator, workflow template) fails fast
  instead of being ignored.
- GraphQL field names map to database columns in mutation SQL, tree-sync and filters.
- The `bifrost` CLI registers every dialect.

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

## Before 0.4.1

Changes from `3c42a60` up to 0.4.1.

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
