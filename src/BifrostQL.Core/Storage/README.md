# File object storage

Two layers live here:

- **`FileStorageService` + `IStorageProvider`** — the pre-existing file-column
  plumbing behind the GraphQL `_upload`/`_download`/`_delete` resolvers. Resolves
  a column's bucket config, enforces size/MIME limits, and moves bytes.
- **`FileObjectSeam` + `S3ObjectKeyMap`** — the programmatic file-object seam a
  non-GraphQL front door (the S3 adapter) uses. This is the layer that owns
  authorization; the section below is its contract.

## FileObjectSeam contract

### Addressing

| S3 concept | Bifrost concept |
|---|---|
| bucket | table (name lowercased) |
| object key | `{file column}/{pk0}/{pk1}/…` |
| object | one file column's value on one row |

Bucket = `table.DbName.ToLowerInvariant()`. Lowercasing is the only
normalization, and its collisions are **detected, not collapsed**: two tables
differing only by case make that bucket ambiguous and resolution throws rather
than serving one of them. A table whose lowercased name is not a legal S3 bucket
name is not addressable over S3 at all — an honest rejection, never a rewrite
into a name that might collide with another table.

Object keys are **deterministic and injective**. Each component is percent-encoded
over the unreserved set `[A-Za-z0-9_-]` and joined with `/`. Since the escape is
injective and can never emit `/`, the join is injective on the tuple — two
distinct rows can never collapse onto one key, composite keys included. Traversal
is impossible *by construction* rather than by filtering: `.` escapes to `%2E`, so
no component can emerge as a `.` or `..` segment; keys are never absolute; and
empty components are rejected (an empty path segment is silently swallowed by
`Path.Combine`, which is itself a collapse).

`FileMetadata.GenerateFileKey` is deliberately **not** used for this: it is
non-deterministic (timestamp + random suffix), so a GET could never re-address the
object a PUT wrote, and its sanitizer rewrites every invalid character to `_`,
collapsing the distinct ids `a/b` and `a_b`.

### Authorization

Reads go through `IQueryIntentExecutor`; writes go through
`IMutationIntentExecutor` (hence the full `TableMutationPipeline`). The seam
supplies **only** the positional primary key plus the caller's `UserContext` and
builds **no predicate of its own** — the pipeline narrows scope from the identity,
so an out-of-scope key matches zero rows structurally rather than because the seam
remembered to filter.

A storage path or stream is reachable only from a `ResolvedFileObject`, which has
no public constructor and can be minted only by `ResolveAsync` — the identity-gated
read. No adapter-facing API can skip it. (Honest bound: construction is `internal`,
so first-party code inside Core and its `InternalsVisibleTo` list could fabricate
one — but that code could call the storage provider directly anyway. The seam
constrains the adapter's API surface; it is not a sandbox against Bifrost's own
internals.)

Only columns configured for file storage are addressable, so the seam cannot be
turned into a download endpoint for arbitrary columns.

Writes are **off by default** (`FileObjectSeamOptions.EnableWrites`), and the gate
is the first check in `PutAsync`/`DeleteAsync` — before key parsing or model
lookup — so a disabled surface builds zero intent and cannot be probed for
behaviour. Enabling it logs a startup warning.

`ResolveAsync` returns null identically for "row not visible", "row absent", and
"row holds no file", so the seam is not a row-existence oracle.

### Object metadata

| Field | Source |
|---|---|
| content type | client-supplied at PUT, persisted on the row; validated against the bucket + column MIME allow-lists (a configured allow-list with no content type is **rejected**, not bypassed) |
| content length | byte length recorded at PUT |
| ETag | lowercase hex MD5 of the content, computed once at PUT and persisted. MD5 is not a security primitive here — it is the literal definition of an S3 ETag for a single-part object. Recorded at write time so serving an ETag never costs a download. Null on rows written before this field existed. |
| custom metadata | caller-supplied at PUT, persisted in the row's file-pointer JSON |
| last modified | upload timestamp |

The storage *target* (bucket/provider) always comes from the column's
configuration, never from the row-persisted `BucketName`/`ProviderType`, which are
ordinary writable column values an attacker could repoint.

### Failure ordering

**PUT** — resolve the row (proving it exists and is visible) **before any storage
access** → upload the blob → update the pointer through the mutation pipeline
(which may still veto). A veto or failure at the final step triggers a
compensating delete of the just-uploaded blob: an orphaned blob the caller was
told nothing about is the one outcome with no owner. If the compensating delete
*also* fails, both failures are surfaced together — never swallowed — because the
residue then needs an operator.

Because keys are deterministic, re-putting an object overwrites in place and
accumulates no orphans.

**DELETE** — resolve → clear the pointer through the mutation pipeline **first** →
delete the blob. This is the opposite of `FileDeleteResolver`'s blob-first order,
and the divergence is deliberate: the pipeline is the authorization gate and it may
veto, so deleting the blob before it runs would destroy content the veto was
supposed to protect — unrecoverable. Clearing the pointer first bounds the worst
case to a blob no row references (invisible to every Bifrost surface, reclaimable
by a sweeper) instead of a row advertising content that is already gone. A failing
blob delete after a cleared pointer still throws: the residue is real.

**Deleting an object is an `Update` intent, not a `Delete` intent.** An S3 object
is a *column value*, not a row; a Delete intent would destroy (or soft-delete) the
whole row and every other column on it. Whether the row-clearing update is itself
audited, soft-deleted, or vetoed remains the pipeline's decision.

### Known gaps (deliberate)

- No HTTP/S3 codec, no SigV4, no multipart upload, no bucket administration.
- No DI registration yet — a host constructs `FileObjectSeam` directly.
- Objects written before this seam existed carry a random `GenerateFileKey` key.
  Re-putting one writes the new deterministic key and leaves the old blob behind;
  reclaiming those is a sweeper's job, not the write path's.
- A table whose name is not a legal bucket name is unaddressable; an explicit
  bucket-name override would need a new metadata key.
