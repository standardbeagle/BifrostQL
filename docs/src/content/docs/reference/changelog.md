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

On `main` after v0.4.28; not yet on NuGet.

### Breaking — third review pass

- **S3 errors are mapped by condition code, not exception type.** Only a missing or unreadable address (`NOT_FOUND`, `ACCESS_DENIED`) answers `404`; a write conflict (`CONFLICT`, `EXCLUSION_VIOLATION`) answers `409 OperationAborted`; anything else (pool exhaustion, a disposed service, a database fault) answers `500 InternalError` and is logged at Error. Before, every `InvalidOperationException` and `BifrostExecutionError` became `404` and was logged at Debug. `DeleteObject` no longer answers `204` when the row is visible but nothing was deleted: a zero-row write is `404`, a conflict `409`, a fault `500`. `FileObjectSeam` tags its own not-found conditions with the new `BifrostExecutionError.NotFoundCode` (`NOT_FOUND`) instead of throwing `InvalidOperationException`.
- **S3 SigV4 requires every `x-amz-*` request header to be signed.** A request carrying an unsigned `x-amz-*` header (including `x-amz-content-sha256`, `x-amz-copy-source`, `x-amz-meta-*`) is refused with `403 AccessDenied`. Clients that sign with the AWS SDKs or rclone already do this.
- **S3 continuation tokens expire** after `S3Options.ContinuationTokenTtl` (default 15 min). The token format changed (`s3ct2`), so tokens issued before the upgrade are refused once.
- **Stored-procedure config is removed.** BifrostQL never loaded or executed stored procedures; the keys were accepted and did nothing. `sp-include`, `sp-exclude` and `stored-procedure-role` are now unknown `:root` keys, so a model that still sets one fails at load: delete them. `IDbModel.StoredProcedures`, `DbStoredProcedure` and the unused `MultiDbConfig` / `DatabaseFieldConfig` / `CrossDbJoinConfig` types are removed, and the `DbModel.FromTables` overloads lose their stored-procedure parameter. Nothing public replaces them; multi-database hosting stays on `BifrostMultiDbOptions`.
- **`Decimal` is sent as a decimal string** (`"19.99"`), not a JSON number, so values past a double's 15–17 digits keep every digit. Input still accepts a number or a string. Clients that read `Decimal` fields as numbers must parse the string. On `<table>Aggregate`, `_sum`/`_avg`/`_min`/`_max` over a decimal column are typed `Decimal` instead of `Float`. The proto schema maps `decimal` to `string`, so `@bifrostql/codegen` emits `string` for it.
- **Per-row `_agg` returns `AggregateNumber`, not `Float`,** written by the same rule as `<table>Aggregate`: `count` is a JSON integer, and `sum`/`avg`/`min`/`max` over a decimal or bigint column is an exact decimal string (`"1029.98"`); other numeric columns stay JSON numbers. Typed `Float`, a bigint SUM past 2^53 or a NUMERIC(38,10) SUM reached the client rounded. Clients that read `_agg` over a decimal or bigint column as a number must parse the string. On `<table>Aggregate`, a bigint column's `_sum`/`_avg`/`_min`/`_max` field is now `Decimal` instead of `Float`, for the same reason.
- **`@bifrostql/codegen` maps proto `bytes` to a base64 `string`** (was `Uint8Array`), matching what the server writes into JSON.
- **Binary transport:** a full ack window pauses a chunked response instead of closing the connection after 30 s. `ChunkSender.DefaultAckTimeout` is removed; use `ChunkSender.NoAckTimeout` or pass `UseBifrostBinary(ackTimeout:)`, which drops only the stalled response with `CHUNK_ACK_TIMEOUT`.
- **`TreeSyncStateLoader.LoadAsync` takes an open `DbConnection`** instead of an `IDbConnFactory`; tree-sync now loads its diff inside the write transaction.
- **The AppMetadata, Blob and SavedObjects mounts inherit the endpoint's `DisableAuth`** through `MountAuthRequirement`. An anonymous endpoint now serves them anonymously unless `RequireAuth = true`; with no identifiable endpoint they fail closed.
- **Local auth keeps server-side sessions.** Cookies issued before the upgrade carry no session id, so every user signs in again once. Sessions end after 15 idle minutes by default (8 h absolute is unchanged). A role, tenant or household change ends the user's sessions instead of updating them.
- **`AddBifrostLocalAuth` needs DDL and write rights** for its `_bifrost_auth_*` tables, created at host start. Missing rights fail the start, not the first login. Register your own `ILocalAuthStateStore` to keep them elsewhere.
- **Local password hashes use PBKDF2-HMAC-SHA512 at 210,000 iterations** (2.1× the work per login). Existing hashes upgrade at the next sign-in.
- **`LocalAuthOptions.PasswordHashIterations` below 210,000 fails at startup** (was below 100,000). `MinPasswordHashIterations` now equals the OWASP figure, so configuration can no longer weaken the default. Tests that need cheap hashing pass an `IPasswordHasher<string>` to the `LocalUserStore` constructor.
- **OData refuses Basic credentials on plain HTTP** with 403 ("Basic credentials are accepted only over HTTPS.") before reading them. Set `ODataOptions.AllowCleartextBasicAuth` to opt out (off by default; a startup warning when on). `X-Forwarded-Proto` counts only when the host configures ForwardedHeaders.
- **A read-denied table answers like a missing one** on RESP (`-ERR unknown table`, was `-NOPERM`), pgwire (42601 `relation "<name>" does not exist.`, was 42501), LDAP (`noSuchObject`, and subtree searches skip denied families; was `insufficientAccessRights`) and gRPC (UNIMPLEMENTED "Method is unimplemented.", was INVALID_ARGUMENT / PERMISSION_DENIED). OData, MCP, S3 and feeds already matched and are now pinned. GraphQL and the binary transport still differ: the schema is not filtered per identity.
- **SQL Server `timestamp` / `rowversion` columns are row versions, not date-times.** They are exposed as base64 binary, left out of insert input, and usable as a `concurrency-token`. Postgres, MySQL and SQLite `timestamp` keep their date-time meaning.
- **Field-encryption key APIs are async.** `IDataEncryptionKeyStore` has only `LoadAsync`/`StoreAsync`, so custom key stores must implement them. `EnvelopeKeyManager` key methods are `*Async` (`GetDataKeyAsync`, `RotateAsync`, …), `CryptoReadProjector` is built with `CreateAsync`, and `TableReadChain.For` is `ForAsync`. A key version minted by another process is picked up within about a second.
- **`RowScopeCompiler.Compile` takes an `ITypeMapper`**, so row-scope comparisons convert context values by the column's dialect type.
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
- A number in a `JSON` column variable with more digits than a decimal or double holds (`0.1234567890123456789012345678901234567891`) is stored as sent. GraphQL.SystemTextJson's variable reader parsed it to a decimal, rounding it to 28–29 digits; `AddBifrostQL` now registers `ExactNumberInputsJsonConverter`, which keeps such a number as its own JSON token. `Float` and `Decimal` variables still accept it and round it as before. A host that builds its own `GraphQLSerializer` adds the converter to keep the fix.
- Tree-sync reads the existing rows under an update lock inside its write transaction, so a concurrent writer can no longer change the diff between load and write.
- Binary transport: Error frames carry a machine-readable `error_code` (protobuf field 13); the client requeues concurrency refusals by code. A stream no longer times out while its consumer still holds received chunks. Requests over 4 MiB are sent as chunks the server reassembles (up to 64 MiB); larger ones are refused before sending.
- edit-db: grouping reports truncation under any `max-query-rows`, including ceilings below 1001, and names the real group count.
- edit-db: a Boolean key, filter or field value other than true/false/0/1 is refused with a clear error instead of being read by truthiness.
- edit-db: a composite foreign key's header filter selects one parent row and filters on every FK column; the `cf` URL parameter adds a `_fk_tuple` form and still reads old links.
- edit-db and `@bifrostql/react`: grouping, sort, aggregates and number/percent formats read `Decimal` strings exactly.
- EAV `_meta` is capped per parent at `max-query-rows - 1` attributes, windowed in SQL. A parent with more returns the first ones in key order plus `"__truncated": true`; before, it returned every attribute row however many there were. An attribute keyed `__truncated` is now refused.
- Local auth: a login, password-change or administration request whose body is not JSON (another content type, or an undecodable charset) answers 415 instead of 500.
- Local auth: the administration endpoints refuse a `userId` longer than 128 characters with 400 before acting; it could not fit the audit column, so the action ran and the audit write failed after it.
- Local auth: the email claim is the login column's stored value, not the caller's spelling of it under a case-insensitive collation.

### Changed — third review pass

- EAV `_meta` reads a whole page's attributes in one query per 500 parents instead of one per row (1000 rows: 752 → 172 ms on SQL Server, 1099 → 132 ms on Postgres). New public seam `IBatchComputedColumnProvider`.
- Nested collections two or more levels deep restrict to the parent page's rows instead of every child of the root page (about 8–13× faster on a 3-level fixture).
- Parsed table policy and computed-column config are memoized per table and invalidated on any metadata write (about 48 µs → 0.5 µs per lookup).
- `DbTable`, `ColumnDto` and `DbModel` copy a plain dictionary handed to `Metadata` in an initializer instead of wrapping it, so a later write through the caller's reference no longer reaches the object (it used to, without invalidating the memo above). Write through `.Metadata` instead.
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
