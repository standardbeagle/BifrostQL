---
title: "Changelog"
description: "User-facing behavior changes, including breaking changes and how to migrate."
---

Notable user-facing changes to BifrostQL. Breaking changes list the migration step.

## Unreleased

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
