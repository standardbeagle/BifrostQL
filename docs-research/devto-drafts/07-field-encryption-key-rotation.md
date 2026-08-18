---
title: "Field encryption with online key rotation"
published: false
description: "AES-256-GCM column encryption declared in metadata: blind-index equality search, per-role masking, and a versioned ciphertext envelope that lets you rotate the data key while reads keep serving."
tags: security, encryption, dotnet, database
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/field-encryption/
---

Encrypting a database column is the easy part. The hard parts arrive afterward: you
still need to search that column, you still need to show a partial value to support
staff, and eventually you need to change the key while the application keeps serving
traffic.

BifrostQL handles all three from column metadata. Here is the whole declaration:

```text
dbo.customers.ssn {
  encrypt: aes-256-gcm
  key-ref: config:pii
  mask: last4
  unmask-role: compliance
  blind-index: ssn_bidx
}
```

That gives you AES-256-GCM ciphertext in `ssn`, an HMAC token in `ssn_bidx` so
equality search still works, plaintext for the `compliance` role and `***-**-6789`
for everyone else, and a rotation path that never takes reads down. Below is what
each of those costs and where the sharp edges are.

## Three keys, not one

```
root key (KMS or config)      ← wraps DEKs; never encrypts field data
   └─ data-encryption key (DEK, one per key-ref)   ← encrypts field values
        └─ blind-index key (HKDF-SHA-256 from the DEK)   ← keyed hash for search
```

The root key is 32 bytes from an `IRootKeyProvider`. It only ever wraps DEKs. A DEK is
a random 32-byte key minted on first use of a `key-ref` and stored **wrapped**
(AES-256-GCM, with the key-ref bound as AAD), so a plaintext DEK never reaches disk.
The blind-index key is HKDF-derived with its own label, which keeps the deterministic
search key separate from the key that encrypts the value.

Both halves are required in DI — an `IRootKeyProvider` and an
`IDataEncryptionKeyStore`. If either is missing, the key manager resolves to null and a
write to an encrypted column is refused rather than silently storing plaintext.

Nothing auto-registers an in-memory key store, and that omission is deliberate.
`InMemoryDataEncryptionKeyStore` exists for tests; as a default it would drop the
wrapped DEKs on restart and make every encrypted value permanently unreadable.

## The cell binding that stops ciphertext transplants

Each value gets a fresh random 12-byte nonce and a 16-byte GCM tag, so two rows holding
the same SSN store completely different bytes. There is no equality oracle sitting in
the stored ciphertext.

GCM also authenticates additional data without encrypting it, and the AAD here is the
length-prefixed `schema`, `table`, `column`. Copy an encrypted value from
`customers.ssn` into another column and decryption fails, because the AAD no longer
matches. That closes the "paste an admin's encrypted value somewhere I can read"
attack.

The binding is column-scoped rather than per-row. For a database-generated primary key
the id does not exist yet at encrypt time, since encryption runs before the INSERT that
mints it, so binding to the row key would make write and read asymmetric. Per-row
binding is a planned enhancement.

Encryption runs as a mutation transformer at priority 40, inside the security band:
after tenant and policy pinning, before soft-delete. Plaintext exists in that narrow
window and every downstream transformer sees only the envelope. One consequence: a
`pattern` or `min-length` validator on an encrypted column runs later and would be
validating ciphertext. Validate the plaintext in your application layer.

The round-trip is pinned against a real SQLite database, checking the raw stored bytes:

```csharp
var (ssn, bidx) = await ReadRawAsync(1);
ssn.Should().NotBeNull().And.NotBe(plaintext, "the column stores ciphertext, never the plaintext");
var dek = manager.GetDataKey(KeyRef);
var aad = CryptoAad.Build("main", "secrets", "ssn");
FieldCipher.Decrypt(dek, ssn!, aad).Should().Be(plaintext);
```

On the GraphQL side, `{ secrets { data { ssn } } }` as the `compliance` role returns
`123-45-6789`; the same query as a `viewer` returns a value asserted to end with `6789`
and to not contain `123-45`. Raw ciphertext never leaves the process — if decryption is
impossible for any reason, the projector redacts instead of falling back to the stored
bytes.

## Searching without leaking

The blind index is a sibling column holding an HMAC-SHA-256 token of the plaintext under
the derived key. On write both columns are filled. On read, the query transformer
rewrites the predicate before the column guards run, so the generated SQL compares the
sibling and the ciphertext column never appears in a WHERE clause:

```csharp
// The predicate targets the blind-index sibling, never the ciphertext column.
rendered.Sql.Should().Contain("\"ssn_bidx\"").And.NotContain("\"ssn\"");
var expected = BlindIndexComputer.ComputeSearchToken(manager, KeyRef, "123-45-6789");
parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(expected);
```

Exactly two operators route: `_eq` with a value, and `_in` (one token per element).
Everything else is refused — `_neq`, `_gt`, `_lt`, `_contains`, `_between`, sorts,
aggregates. An encrypted column with no `blind-index` sibling refuses `_eq` as well, so
a half-configured table gives no partial oracle, and a missing key manager makes the
rewrite refuse the query rather than emit a raw predicate.

A blind index answers "is this exact value present". It will never answer "which values
are near it", and the refusals are what keep that true.

Tokens are computed from the value exactly as written, with no case or whitespace
normalization. If you want case-insensitive matching, normalize on the way in.

## Rotation: the key version rides inside the ciphertext

To rotate a DEK while old rows still exist, every value has to say which key opens it.
The envelope carries that:

```
Legacy format (pre-rotation writes):
  [format=1][nonce:12][tag:16][ciphertext:n]

Versioned format (all writes now):
  [format=2][keyVersion:4][nonce:12][tag:16][ciphertext:n]
```

```csharp
var envelope = new byte[1 + KeyVersionSize + NonceSize + TagSize + cipherBytes.Length];
envelope[0] = FormatVersioned;
BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(1, KeyVersionSize), keyVersion);
```

The leading byte is the envelope format, and the four bytes after it are the key version.
Legacy format-1 envelopes carry no version and decrypt under the key-ref's original DEK,
which is version 1, so data written before rotation existed keeps working.

Reads are version-directed: the reader pulls the version out of the envelope and resolves
that one DEK. There is no trial decryption against a candidate set. A value names the
single key that can open it.

`EnvelopeKeyManager.Rotate("config:pii")` mints the next version and makes it current for
new writes, and every prior version stays resolvable from the store. Two concurrent
rotations computing the same next version converge on one persisted DEK,
first-writer-wins.

The blind-index key does **not** move. It is HKDF-derived from version 1 and pinned there,
so search tokens stay stable across every rotation and rows encrypted under different DEK
versions still hash-match the same query.

## Moving old rows without a pipeline bypass

`CryptoReEncryptionSweep` walks rows, skips any value already on the current version or
`NULL`, decrypts the stale ones version-directed, and issues an Update through
`IMutationIntentExecutor` carrying the positional primary key plus the plaintext. The
encrypt-on-write transformer in the pipeline puts it back under the current key.

The security properties come from what the sweep declines to do. It writes no SQL and
builds no predicate — it supplies the key and the value, and the pipeline narrows the
update from the caller's `userContext`. Run it with no tenant context against a
tenant-filtered table and it re-encrypts nothing, which is the fail-closed outcome rather
than a global cross-tenant rewrite. To sweep a tenant-scoped table, run it per tenant.

It also counts honestly: `RowsAffected` comes from `MutationIntentResult.AffectedRows`,
the real affected-row count. On a single-key table the update's return value is the
primary key, so reading that as a count would report a scoped-away write as a success.

A half-swept table serves both generations at once:

```csharp
FieldCipher.PeekKeyVersion(raw[id1]!).Should().Be(2, "the swept row is now on the current key");
FieldCipher.PeekKeyVersion(raw[id2]!).Should().Be(1, "the un-swept row is left on the old key (half-swept)");
```

All rows in that test still decrypt while the table is in that mixed state. Skipping
already-current values also makes the sweep idempotent, so re-running it over a partly
swept table is safe.

A caller without the `unmask-role` sees the mask throughout, and cannot observe the key
version at all, since the version lives inside ciphertext they never receive.
Filterability is identical before, during, and after rotation for every role, so the
rollout opens no side channel for telling an old-key row from a new-key one.

## What I ran, and three gaps I found

Forced a rebuild of `src/BifrostQL.Core` first, since an incremental test run can load a
stale assembly and quietly prove nothing:

```
Passed!  - Failed: 0, Passed: 101, Skipped: 0, Total: 101, Duration: 1 s
```

That is the broad crypto filter; the six named crypto classes account for 72 of them. The
cross-dialect binary round-trip against SQL Server, Postgres, and MySQL is a
`[SkippableTheory]` needing live database env vars, so it skipped here and is not in that
101.

Three things the docs currently oversell, worth knowing before you plan a rotation:

1. **The sweep has no batching, cursor, or checkpoint.** The caller hands it an
   `IEnumerable` of rows. "Resumable" means idempotent, so a re-run is safe, but nothing
   tracks progress for you.
2. **There is no hosted sweep service.** No production code path calls
   `CryptoReEncryptionSweep`; it is library code an operator drives. Budget the job
   runner yourself.
3. **Root-key rotation has no shipped API.** The documented procedure — re-wrap each DEK
   under the new root, swap the `IRootKeyProvider` — describes something you would write.
   No re-wrap or root-rotate method exists in the codebase.

The DEK rotation path and everything above it is real and tested. The operational
scaffolding around the sweep is the part you supply.
