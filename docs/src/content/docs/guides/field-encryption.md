---
title: "Rotating Field-Encryption Keys"
description: "Rotate a data-encryption key and re-encrypt live rows with no read downtime, using version-tagged ciphertext and an online sweep through the mutation pipeline."
---

This guide covers **key rotation** for [field-level
encryption](/BifrostQL/concepts/field-encryption/). If you have not set up
encrypted columns yet, read the concept page first — this page assumes you
already have a `key-ref` with encrypted data.

Rotation is **online**: a rotated key-ref keeps serving reads for every existing
row while new writes move to the fresh key, and a background sweep re-encrypts
the older rows in place. No read ever fails and no window returns ciphertext as
plaintext.

## The two kinds of rotation

| Rotation | What moves | Re-encrypt data? |
|----------|-----------|------------------|
| **Root-key** | Re-wraps every DEK under the new root key | No — field data is untouched |
| **DEK** | Mints a new data-encryption key version for a `key-ref` | Yes — via the online sweep |

**Root-key rotation** swaps the base secret the DEKs are wrapped with. Because
the root key only ever wraps DEKs (it never encrypts field values directly),
re-wrapping the DEKs is enough — the stored ciphertext does not change. Rotate
the root key by re-wrapping each stored DEK under the new root and swapping the
`IRootKeyProvider`; no sweep is needed.

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
`CryptoReEncryptionSweep` moves them onto the current version:

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
- **Cannot filter, sort, or aggregate on an encrypted column at all.** Every such
  predicate is rejected. There is no `_eq`/`_in` exception routed to the blind
  index — server-side blind-index read routing is not implemented, so the
  rejection is a blanket one.
- **Cannot observe the key version.** The key version lives inside the ciphertext,
  which a denied role never receives (it gets the mask). Rotation does not change
  timing, error behavior, or filterability for a denied caller, so it opens no new
  side channel to distinguish an old-key row from a new-key row.

## Ciphertext storage across databases

The envelope is opaque binary — a random nonce, tag, and ciphertext plus the
embedded key version. It round-trips verbatim through each dialect's binary
column type (SQL Server `VARBINARY`, Postgres `BYTEA`, MySQL `VARBINARY`, SQLite
`BLOB`) and through the base64-text form used in text columns, and still decrypts
after the round-trip. Do not put a `pattern` / `min-length` validator on an
encrypted column: encryption runs before format validation, so the validator
would see ciphertext, not plaintext.
