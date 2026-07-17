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

**An object key is an address, not a storage key.** The two are deliberately
different things: the key deterministically identifies the *row*, and the row's
pointer binds it to wherever the bytes actually live — a fresh random
`GenerateFileKey` per write. Writing the bytes *at* the address would make every
upload an in-place overwrite of the row's current content, i.e. destructive before
the mutation pipeline has authorized anything. Keeping them separate is what makes
a denied PUT non-destructive; see **PUT** below.

`FileStorageService.UploadFileAsync` therefore exposes **no storage-key
parameter** — the key is always a fresh `GenerateFileKey`. A caller holding a
deterministic address cannot ask for the bytes to be written at it, so this rule
is enforced by the signature rather than by every future call site remembering
it.

**`FileStorageService` is the only sanctioned upload path.** A raw
`IStorageProvider.UploadAsync(config, anyKey, …)` writes bytes at a caller-chosen
storage key and so drops the address/storage-key decoupling above (invariant 8a).
To keep that path from being discoverable, `StorageProviderFactory.GetProvider`
is `internal`, not public: no code outside Core and its friend assemblies can
resolve a provider through the factory and write around the guarantee. Every
production caller of `GetProvider` already lives in Core (`FileStorageService`
and the file-folder computed columns), so nothing public is lost. The **honest
bound** is the same one the seam carries elsewhere: `InternalsVisibleTo` includes
`BifrostQL.Server` (where the S3 adapter lives), so first-party code in a friend
assembly *can* still reach a provider directly — it could equally forge a
`ResolvedFileObject`. The narrowing constrains the public API surface (an
external adapter cannot bypass the sanctioned path), it is not a sandbox against
Bifrost's own internals.

### Authorization

Reads go through `IQueryIntentExecutor`; writes go through
`IMutationIntentExecutor` (hence the full `TableMutationPipeline`). The seam
supplies **only** the positional primary key plus the caller's `UserContext` and
builds **no predicate of its own** — the pipeline narrows scope from the identity,
so an out-of-scope key matches zero rows structurally rather than because the seam
remembered to filter.

A storage path or stream is reachable only from a `ResolvedFileObject`, which has
no public constructor and can be minted only by `ResolveAsync` — the identity-gated
read. There is no *public* signature that hands out a stream without one, so an
adapter cannot forget the check.

**The honest bound on that guarantee:** it is enforced against the **public API
shape**, not against adversarial code in `BifrostQL.Server`. `ResolvedFileObject`
construction is `internal`, and `InternalsVisibleTo` includes `BifrostQL.Server` —
which is where slice 2's S3 adapter lives. That assembly *can* forge one. So the
correct claim is "a caller cannot obtain a stream without resolving a row"
**only for code outside Core's friend assemblies**; for the adapter itself the seam
is a well-shaped API, not an enforcement boundary. Server-side code that wanted to
bypass authorization could call the storage provider directly regardless, so this
is a statement about what the seam *does* prevent (forgetting the gate), not about
what it *could* prevent (deliberately evading it).

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

The upload targets a **fresh random storage key**, never the caller-supplied
address. This is load-bearing, not incidental: the read-visibility check is not an
authorization decision (the mutation pipeline is), so a caller who can merely *see*
a row can still reach the upload. Writing at the address would let that caller
overwrite the row's content in place, and the compensating delete would then remove
it — an unauthorized caller destroying content *and* orphaning the pointer. A fresh
key makes the compensating path incapable of touching a pre-existing blob, so the
protection is structural rather than a matter of doing things in the right order.

Once the new pointer has **committed**, the superseded blob is reclaimed, so no
orphan accumulates across **sequential** writes to a row. That reclaim is
best-effort and logged, never thrown: the write already succeeded and the row is
correct, so failing the call would report a committed write as failed and invite
a retry.

Concurrent PUTs to the same row **do** orphan a blob: each uploads to its own
fresh key before either pointer commits, so the loser's blob is left unreferenced
— the winner reclaims only the blob its own resolve observed, which is the
pre-existing one, not its rival's. This is a storage leak, not a correctness or
authorization defect: the row ends up pointing at exactly one of the two uploads,
and the orphan is invisible to every Bifrost surface. It is left for the same
out-of-band collection as the failed-reclaim residue below.

A zero-affected-row update is treated as failure, not success — read from
`MutationIntentResult.AffectedRows`, never from the pipeline's return `Value`,
which on a single-key table is the primary KEY rather than a count.

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
- Reclaiming a superseded blob is best-effort: if the delete fails after the new
  pointer committed, the old blob is left unreferenced (logged as a warning) for
  out-of-band collection. Failing the caller's committed write instead would be a
  worse trade.
- A table whose name is not a legal bucket name is unaddressable; an explicit
  bucket-name override would need a new metadata key.
