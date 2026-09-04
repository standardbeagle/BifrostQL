---
title: "Changelog"
description: "User-facing behavior changes, including breaking changes and how to migrate."
---

Notable user-facing changes to BifrostQL. Breaking changes list the migration step.

## Unreleased

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
