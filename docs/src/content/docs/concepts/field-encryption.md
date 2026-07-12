---
title: Field-Level Encryption & Masking
description: Encrypt sensitive columns at rest with envelope encryption (AES-256-GCM), bind each ciphertext to its cell, search encrypted columns via a blind index, and mask values per role — the design and key hierarchy.
---

BifrostQL can encrypt sensitive columns **at rest** and reveal them only to
authorized roles, masking or redacting the value for everyone else. This is the
design and key-management foundation; the encrypt-on-write transformer and the
decrypt/mask-on-read guard are later slices.

## Key hierarchy (envelope encryption)

Encryption uses a two-level key hierarchy so keys can rotate without re-encrypting
data:

```
root key (KMS or config)      ← wraps DEKs; never encrypts field data directly
   └─ data-encryption key (DEK, one per key-ref)   ← encrypts field values
        └─ blind-index key (HKDF-derived from the DEK)   ← keyed hash for search
```

- The **root key** is 32 bytes, supplied by a provider (`config` today — a base64
  secret injected at deploy; `kms` is a provider seam for later). It only ever
  wraps (encrypts) DEKs.
- A **DEK** is a random 32-byte key, generated on first use of a `key-ref`,
  stored **wrapped** (AES-256-GCM, with the key-ref bound as AAD) so the plaintext
  DEK never touches disk. Rotating the root key re-wraps DEKs; it does not touch
  the encrypted data.
- The **blind-index key** is HKDF-SHA-256-derived from the DEK, so the
  deterministic search key is separate from the key that encrypts the data.

## Algorithm

Field values are encrypted with **AES-256-GCM** (authenticated encryption). Each
value gets a fresh random 12-byte nonce, so **equal plaintexts produce different
ciphertext** — there is no equality oracle in the stored ciphertext.

### Cell binding (AAD)

GCM authenticates *Additional Authenticated Data* without encrypting it. Each
field's AAD binds the ciphertext to its **column** (length-prefixed
`schema`, `table`, `column`). Because the AAD is authenticated, a ciphertext
**cannot be relocated to another column or table** — decryption fails — closing
the "paste an admin's encrypted SSN into another column" attack.

The binding is column-scoped, not per-row: the primary key is not known at
encrypt time for a database-generated key (encryption runs before the INSERT that
mints the id), so binding to it would make write and read asymmetric. Per-row
binding is a planned enhancement (a post-insert re-encrypt, or an AAD-kind flag in
the envelope).

The stored envelope is base64 of `[version:1][nonce:12][tag:16][ciphertext:n]` —
the format version, nonce (IV), and tag travel with the ciphertext in one column,
so no separate IV/version columns are needed.

## Encrypting on write

Encryption happens in a mutation transformer (priority 40, security band): it runs
after tenant/policy pinning and before soft-delete, so plaintext is confined to
the security band and every downstream transformer and the SQL layer see only
ciphertext. On INSERT/UPDATE it replaces each marked column's plaintext with the
envelope and fills the `blind-index` sibling column.

If a column is marked `encrypt` but no key manager is configured, the write is
**refused** (fail-closed) rather than storing plaintext. Wire the key manager by
registering an `IRootKeyProvider` (e.g. `ConfigRootKeyProvider`) and an
`IDataEncryptionKeyStore` in DI; BifrostQL composes the `EnvelopeKeyManager` from
them.

:::danger
The `InMemoryDataEncryptionKeyStore` loses its wrapped DEKs on restart, which makes
all data encrypted under them **permanently unreadable**. Use it only for tests or
throwaway dev. Production must register a durable `IDataEncryptionKeyStore` (a DB
table or the KMS). BifrostQL deliberately does not auto-register an in-memory store.
:::

Because encryption runs early (priority 40) and server-side format validation runs
late, a `pattern` / `min-length` validator on an *encrypted* column would validate
the ciphertext, not the plaintext — so do not put format validators on encrypted
columns; validate the plaintext at the application layer instead.

## Reading: decrypt or mask

On read, a caller holding the column's `unmask-role` (or the admin role) receives
the decrypted plaintext; every other caller receives the masked value per the
column's `mask` mode (`redact`, `last4`, `email`). The raw ciphertext is never
returned — if decryption is impossible (no key manager, wrong key, tampered value)
the projector redacts, so a misconfiguration hides the value rather than leaking
ciphertext. `last4`/`email` masking decrypts server-side to derive the masked form;
only the masked value leaves the process.

## No plaintext oracle

An encrypted column may be **selected** for output (it is decrypted/masked as
above) but may **not** be used in a `filter`, `_order` (sort), or aggregate
position — those are rejected. A non-deterministic ciphertext used as a predicate
would be either useless or an information oracle (a WHERE that changes the result
set leaks whether a guessed value matches). Equality search is intended to run
through the `blind-index` sibling column; server-side rewrite of an equality
predicate onto the blind index is a planned enhancement (the index is populated on
write today; the query-side routing is the remaining piece).

## Searching encrypted columns

Because the ciphertext is non-deterministic, you cannot filter or sort on it — and
attempting to would leak information, so those paths are rejected (a later slice).
For **equality** search, configure a **blind index**: a sibling column holding a
deterministic keyed hash (HMAC-SHA-256 under the blind-index key) of the plaintext.
Equality queries match on the blind index; the hash is one-way, so the column
still never exposes plaintext.

## Metadata

Configure encryption at the column level:

```text
dbo.customers.ssn {
  encrypt: aes-256-gcm;
  key-ref: kms:pii;
  mask: last4;
  unmask-role: compliance;
  blind-index: ssn_bidx
}
```

| Key | Meaning |
|-----|---------|
| `encrypt` | Algorithm; enables encryption. Only `aes-256-gcm` today. |
| `key-ref` | Which DEK, as `provider:id` (`kms:pii`, `config:pii`). Required. |
| `mask` | What non-unmask-role callers see: `redact` (default), `last4`, `email`. |
| `unmask-role` | Role that may read plaintext. Absent ⇒ only the admin role. |
| `blind-index` | Sibling column holding the deterministic hash for equality search. Must exist. |

Misconfiguration fails fast at model load: an unsupported algorithm, a missing or
malformed `key-ref`, an unknown `mask` mode, a `blind-index` naming a non-existent
column, or any encryption key set on a column without `encrypt`.

## Key rotation

- **Root key rotation** re-wraps each DEK under the new root key — no field data is
  touched.
- **DEK rotation** requires re-encrypting the affected columns (read with the old
  DEK, write with the new). The versioned ciphertext envelope leaves room to run
  old and new DEKs side by side during a rollout. The re-encryption tooling is a
  later slice.
