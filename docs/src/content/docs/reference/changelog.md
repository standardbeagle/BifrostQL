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

### Changed — gRPC, RESP and MCP name operation-id refusals

A gRPC write refused for its operation id answered `INTERNAL`, which clients retry. A write to an
`operation-id: required` table now gets `FAILED_PRECONDITION` with the message
`OPERATION_ID_REQUIRED: this write requires an operation id.` `OPERATION_ID_REUSED` maps to
`ALREADY_EXISTS`; `OPERATION_ID_UNSUPPORTED` and `OPERATION_ID_ANONYMOUS` to `FAILED_PRECONDITION`.
A client that branches on `INTERNAL` for these writes should branch on the new status.

RESP answered the same refusal with `ERR internal error` and MCP with `execution_error`. RESP now
replies `-OPERATION_ID_REQUIRED this write requires an operation id.`, with the code as the error
kind, and MCP returns `OPERATION_ID_REQUIRED: this write requires an operation id.` The other
`OPERATION_ID_*` codes are named the same way on both.

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
