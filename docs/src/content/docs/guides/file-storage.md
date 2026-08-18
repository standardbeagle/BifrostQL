---
title: "Storing Files in Database Columns"
description: "Attach uploads to a table by marking a column as a file column, then keep the bytes on local disk or in a real S3 bucket while the row holds a JSON pointer."
---

A file column stores an upload's metadata in the row and the bytes in a storage bucket.
Mark the column with `file` metadata, point the table at a bucket, and BifrostQL adds
upload, download, and delete operations to the schema. The bytes go to local disk by
default, or to a real S3 bucket with the `BifrostQL.Aws` package.

This is the **outbound** direction: BifrostQL is the client of an object store. For the
opposite direction — object-store clients reading your file columns over the S3 wire — see
[S3-Compatible Object Storage over SQL](/BifrostQL/guides/s3/).

## Declare a bucket and a file column

Storage configuration lives in metadata rules, like every other BifrostQL table setting:

```json
{
  "BifrostQL": {
    "Metadata": [
      ":root { storage: bucket:my-app-files;provider:local;maxSize:10485760 }",
      "dbo.users { storage: bucket:uploads;prefix:avatars }",
      "dbo.users.avatar { file: type:image;maxSize:5242880;accept:image/* }"
    ]
  }
}
```

A bucket config resolves from the column, then the table, then the host default, so a
table can override the model and a column can override its table.

`storage` accepts `bucket`, `provider`, `prefix`, `region`, `endpoint`, `pathstyle`,
`maxsize`, and `mimetypes`. An unknown key or a malformed segment fails model load rather
than being ignored — a typo in a size cap is a security setting that silently stops
applying.

`file` accepts `type`, `maxSize`, `accept`, `thumbnails`, `sizes`, `public`, and `path`.
Where both the bucket and the column cap size, the smaller cap wins.

A column counts as a file column when it carries a `file` tag or when a bucket config
resolves for it. (`file-storage` exists as a legacy marker and no current code path reads
it. Use `file`.)

## What the column holds

The column stores a JSON pointer, not the bytes:

```json
{
  "FileKey": "users/avatar/42_20260817142233123_9f3ac1b7.png",
  "OriginalName": "portrait.png",
  "ContentType": "image/png",
  "Size": 184320,
  "BucketName": "uploads",
  "ProviderType": "local",
  "UploadedAt": "2026-08-17T14:22:33.123Z",
  "AccessUrl": "/srv/uploads/avatars/users/avatar/42_...png",
  "ETag": "9f3ac1b7…",
  "CustomMetadata": null
}
```

The storage key is generated per upload from the table, column, record id, a timestamp,
and eight random hex characters. `BucketName` and `ProviderType` are informational — the
read path resolves the live bucket config from metadata, so moving a bucket does not
require rewriting stored pointers.

## The GraphQL surface

Three fields appear once any table declares storage or a file column:

```graphql
mutation Upload($f: Upload!) {
  _fileUpload(table: "users", column: "avatar", recordId: "42", file: $f) {
    success
    fileKey
    accessUrl
    size
  }
}

query {
  _fileDownload(table: "users", column: "avatar", recordId: "42", expirationMinutes: 15) {
    accessUrl
    contentType
    expiresAt
  }
}

mutation {
  _fileDelete(table: "users", column: "avatar", recordId: "42")
}
```

`recordId` addresses one row. A composite primary key is passed as its key values joined
by `-`, in declared key order, and the count must match exactly.

## How an upload stays safe

The upload path runs the mutation pipeline twice, and the order matters:

1. **Pre-check.** The pipeline runs against the primary key alone. This evaluates tenant
   scope, soft delete, row scope, and table-level policy before a byte is written, while
   leaving required-value validation untripped — the file value does not exist yet.
2. **Reachability.** A `SELECT` confirms the row is visible under the filter the pre-check
   produced. A caller who cannot write the row is refused here, having uploaded nothing.
3. **Size and type checks**, against the smaller of the bucket and column caps and against
   the configured MIME allow-list. A configured allow-list with no supplied content type
   is refused.
4. **Upload to a fresh random key.** The upload API exposes no key parameter, so no caller
   can direct a write at a chosen address.
5. **Write the pointer** through the full pipeline. On failure, or when the update affects
   zero rows, the just-written blob is deleted and the call fails.

Step 4 is what makes step 5's cleanup safe. Because the storage key is random rather than
derived from the caller's arguments, the compensating delete can only remove the blob this
call created. A deterministic key would make the same cleanup delete another row's
content — the failure mode recorded as invariant 8 in
`.claude/rules/protocol-adapter-security.md`.

Storage keys are also checked for traversal. A `..` segment, an absolute path, or a key
resolving outside the bucket and prefix is refused by both providers.

## Local storage

The default provider writes under the bucket name as a filesystem path, with the bucket's
`prefix` and then the file key beneath it. Downloads stream from a real `FileStream`
rather than buffering the whole file.

Presigned URLs from the local provider are opaque `local://<key>` values. Serve local
files through your own authorized endpoint; the local provider deliberately does not hand
out a filesystem path as a URL.

## S3 as the backing store

Add the `BifrostQL.Aws` package and register the provider before building the host:

```csharp
using BifrostQL.Aws;

AwsStorageRegistration.Register();
```

That call adds `s3` to the provider registry. Buckets then select it in metadata:

```text
:root { storage: bucket:my-app-files;provider:s3;region:us-east-1;prefix:uploads }
```

All S3 settings come from the bucket config, so different tables can target different
buckets or regions. Set `endpoint` and `pathstyle` for an S3-compatible service such as
MinIO; leave them unset to use the region endpoint. Credentials come from the standard AWS
credential chain — BifrostQL configures none of its own, so instance roles, environment
variables, and profiles all work as they do for any AWS SDK client.

`_fileDownload` returns a presigned GET URL, expiring in 15 minutes by default.

## Virtual folder columns

`file-folder` publishes a storage prefix as a computed column, which lets a row expose the
objects filed under it:

```text
dbo.projects { file-folder: assets:JSON:local:folder=project-assets/{Id},depends=Id }
```

The placeholder is rendered from the row, `depends` names the columns needed to render it,
and `recursive` walks subfolders. Both the local and S3 providers can list a folder.

## Related

- [S3-Compatible Object Storage over SQL](/BifrostQL/guides/s3/) — serving these same file
  columns to AWS CLI and rclone clients.
- [Row and Column Authorization Policies](/BifrostQL/guides/authorization/) — the checks the
  upload pre-check evaluates.
- [BifrostQL Configuration Reference](/BifrostQL/reference/configuration/) — the full
  metadata key list.
