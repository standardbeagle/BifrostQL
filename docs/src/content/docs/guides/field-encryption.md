---
title: "Rotating Field-Encryption Keys"
description: "Rotate a data-encryption key and re-encrypt rows with no read downtime, using version-tagged ciphertext and an operator-driven sweep through the mutation pipeline."
---

This guide covers **key rotation** for [field-level
encryption](/BifrostQL/concepts/field-encryption/). If you have not set up
encrypted columns yet, read the concept page first — this page assumes you
already have a `key-ref` with encrypted data.

Rotation is **online**: a rotated key-ref keeps serving reads for every existing
row while new writes move to the fresh key, and an operator-driven sweep
re-encrypts the older rows in place. No read ever fails and no window returns
ciphertext as plaintext.

## The two kinds of rotation

| Rotation | What moves | Re-encrypt data? |
|----------|-----------|------------------|
| **Root-key** | Re-wraps every DEK under the new root key | No — field data is untouched |
| **DEK** | Mints a new data-encryption key version for a `key-ref` | Yes — via the online sweep |

**Root-key rotation** swaps the base secret the DEKs are wrapped with. Because
the root key only ever wraps DEKs (it never encrypts field values directly),
re-wrapping the DEKs is enough — the stored ciphertext does not change, and no
sweep is needed. **There is no shipped API for this**: the procedure — re-wrap
each stored DEK under the new root, then swap the `IRootKeyProvider` — is code
you write against your key store; no re-wrap or root-rotate method exists in
the codebase today.

The rest of this guide is about **DEK rotation**, where the key that actually
encrypts field values changes and existing rows must migrate to it.

## Key version travels with the ciphertext

Before rotation, a `key-ref` had exactly one DEK. To let a value written under an
old DEK stay decryptable after a new DEK becomes current, the **key version is
embedded in the ciphertext envelope**:

```
Legacy format (pre-rotation writes):
  [format=1][nonce:12][tag:16][ciphertext:n]

Versioned format (all writes now):
  [format=2][keyVersion:4][nonce:12][tag:16][ciphertext:n]
```

The leading byte is the **envelope format** version, not the key version. Legacy
(format `1`) envelopes carry no key version and decrypt under the key-ref's
original DEK; that DEK is **version 1**, so pre-rotation data is never broken.
Every new write uses the versioned format and stamps the current DEK version into
the four bytes after the format byte.

Reads are **version-directed**: the reader reads the key version out of the
envelope and resolves that exact DEK version to decrypt with. There is **no
trial-decryption** against a set of candidate keys — a value names the one key
that can open it.

## Rotating a DEK

`EnvelopeKeyManager` owns DEK versions for a `key-ref`:

```csharp
// Mint a new DEK version and make it current for new writes.
int newVersion = keyManager.Rotate("config:pii");   // e.g. returns 2

int current = keyManager.GetCurrentVersion("config:pii");   // 2
byte[] v1 = keyManager.GetDataKey("config:pii", 1);  // old DEK — still resolvable
byte[] v2 = keyManager.GetDataKey("config:pii", 2);  // new DEK
```

After `Rotate`:

- New writes encrypt under the new version and stamp it into the envelope.
- **Every prior version stays durably resolvable** so already-written rows keep
  decrypting. Store multiple DEK versions in a durable
  `IDataEncryptionKeyStore` (e.g. `FileDataEncryptionKeyStore`) so they survive a
  restart and the whole sweep.
- The **blind-index key does not change**. It is HKDF-derived from version 1, so
  equality-search hashes stay stable across rotation and rows written under
  different DEK versions still hash-match.

`Rotate` is safe under a concurrent race: two rotations that compute the same
next version converge on one persisted DEK (first-writer-wins in the store),
never two divergent keys.

## The online re-encryption sweep

Rotation alone leaves older rows readable but still on the old key. The
`CryptoReEncryptionSweep` moves them onto the current version. It is **library
code an operator drives** — no hosted service or production code path calls it,
so you supply the job runner that selects rows and invokes it:

```csharp
var sweep = new CryptoReEncryptionSweep(mutationIntentExecutor, keyManager);

CryptoSweepResult result = await sweep.ReEncryptRowsAsync(
    table,           // the IDbTable being swept
    rows,            // raw rows: primary-key columns + current stored ciphertext
    userContext);    // caller identity used to scope the writes

// result.RowsScanned / RowsReEncrypted / RowsAffected
```

For each row, the sweep:

1. Reads the stored ciphertext of each encrypted column and its embedded key
   version. A value already on the current version, or `NULL`, is left untouched
   — so the sweep is **idempotent** and safe to re-run over a half-swept table.
2. Decrypts the stale value **version-directed** (resolving the old DEK version
   named in the envelope).
3. Issues an **Update** through `IMutationIntentExecutor` carrying only the row's
   positional primary key plus the decrypted plaintext. The
   `EncryptOnWriteMutationTransformer` in the pipeline re-encrypts that plaintext
   under the **current** version.

The sweep has **no batching, cursor, or checkpoint** of its own: the caller
hands it the rows as an `IEnumerable`, and "resumable" means idempotent — a
re-run is safe, but nothing tracks progress for you. For a large table, select
and feed the rows in batches yourself.

### Security properties of the sweep

- **No direct SQL, no pipeline bypass.** The re-encryption write goes through the
  full `TableMutationPipeline` (tenant scoping, soft-delete, audit, concurrency),
  exactly like any other write. The sweep has no code path that reaches the
  database without it.
- **No tenant-scope bypass.** The sweep supplies only the primary key and the new
  value — it builds **no predicate of its own**. The pipeline narrows the update
  from the supplied `userContext`, so an out-of-scope row matches zero rows. Run
  with no tenant context on a tenant-filtered table and the sweep re-encrypts
  **nothing** (fail-closed), never a global cross-tenant rewrite. To sweep a
  tenant-scoped table, run it per tenant under that tenant's context. This is the
  same no-caller-identity discipline the retention purge sweep follows.
- **Effective-rewrite counting.** `RowsAffected` comes from
  `MutationIntentResult.AffectedRows` — the real affected-row count — never from
  the update's return value (which is the primary key on a single-key table). A
  write scoped away to zero rows is reported honestly, not as a success.

### Reads during the sweep

A **half-re-encrypted** table — some rows on the new DEK, some still on the old —
serves reads for both throughout. Each value carries its own key version, so the
read path resolves the right DEK per row. Ciphertext is never returned as
plaintext at any point in the rollout.

## What a denied role sees through a rotation

Rotation changes nothing about masking or the no-oracle guarantees. A caller
**without** the column's `unmask-role` (and not the admin role):

- Sees the **masked** value (`redact`, `last4`, or `email`) — never the plaintext
  and never the raw ciphertext, before, during, or after rotation.
- **Cannot sort or aggregate on an encrypted column, and can filter it only by
  equality.** `_eq` and `_in` are rewritten onto the column's blind index (see
  below); every other predicate is rejected. Filterability is identical for
  permitted and denied roles, so the rewrite reveals nothing about a row.
- **Cannot observe the key version.** The key version lives inside the ciphertext,
  which a denied role never receives (it gets the mask). Rotation does not change
  timing, error behavior, or filterability for a denied caller, so it opens no new
  side channel to distinguish an old-key row from a new-key row.

## Searching an encrypted column

Declare a blind index next to the encrypted column to make equality search work:

```text
dbo.customers.ssn {
  encrypt: aes-256-gcm
  key-ref: kms:pii
  mask: last4
  unmask-role: compliance
  blind-index: ssn_bidx
}
```

`blind-index` names a real column on the same table; model loading rejects a name that does
not exist, and rejects a NOT NULL blind-index column whose encrypted source is nullable (a
write that leaves the source NULL computes no token, so the insert could never satisfy the
constraint). The convention across the examples and tests is `<column>_bidx`.

The blind-index column is server-derived and not client-visible in either direction:

- **Writes**: it is omitted from every mutation input type, and the write path rejects a
  direct value for it — a client-supplied token would desync the index from the ciphertext
  or plant a forged one. Keep the column nullable and let the transformer maintain it.
- **Reads**: it is hidden from the GraphQL types, sort enums, and filter inputs, omitted
  from every schema/catalog listing (pgwire, MCP, and the other adapters share one
  projection), and the query path rejects selecting, filtering, sorting, or aggregating it
  directly. The token is a deterministic HMAC — readable tokens would let a caller
  correlate equal hidden values across the rows they can see. The server's own equality
  rewrite is the only sanctioned reference and continues to work.

A policy `read-deny` on the encrypted **source** column also denies `_eq`/`_in` on it:
the equality rewrite records the original column for the read guards, so routing the
predicate through the blind index cannot bypass a column-level read denial.

On write, the encrypt transformer stores the ciphertext in `ssn` and an HMAC-SHA-256 token
in `ssn_bidx`. On read, the query transformer rewrites the predicate before the column
guards run:

```graphql
{
  customers(filter: { ssn: { _eq: "123-45-6789" } }) {
    data { customerId ssn }
  }
}
```

The generated SQL compares `ssn_bidx` against the token for that plaintext. The
ciphertext column never appears in the WHERE clause.

| Operator | Behavior |
|---|---|
| `_eq` with a value | Rewritten onto the blind-index column |
| `_in` | Every element tokenized, rewritten onto the blind-index column |
| `_eq: null` | Left in place — hashing an absent value would leak nothing useful |
| Everything else | Rejected: `_neq`, `_gt`, `_lt`, `_contains`, `_between`, and the rest |

Sorting and aggregating on an encrypted column stay rejected. An encrypted column with no
`blind-index` sibling rejects `_eq` too, so a partially configured table gives no partial
oracle. When the key manager or `key-ref` is missing, the rewrite refuses the query rather
than emitting a raw predicate.

Two properties follow from the key derivation:

- The blind-index key is HKDF-derived from the version-1 DEK with its own label, so it is
  a distinct key from the one that encrypts the value.
- It is pinned to version 1, so **rotation never invalidates a blind index**. Rows written
  under any DEK version still match the same search token.

Tokens are computed from the value as written, with no case or whitespace normalization.
Normalize on the way in when you want case-insensitive matching.

## Ciphertext storage across databases

The envelope is opaque binary — a random nonce, tag, and ciphertext plus the
embedded key version. It round-trips verbatim through each dialect's binary
column type (SQL Server `VARBINARY`, Postgres `BYTEA`, MySQL `VARBINARY`, SQLite
`BLOB`) and through the base64-text form used in text columns, and still decrypts
after the round-trip. Do not put a `pattern` / `min-length` validator on an
encrypted column: encryption runs before format validation, so the validator
would see ciphertext, not plaintext.
