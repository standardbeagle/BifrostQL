---
title: "Database Column Encryption at Rest"
description: "Encrypt database columns at rest with AES-256-GCM envelope encryption, search them by equality through a blind index, and mask the value per role on every read."
---

Database column encryption at rest lets BifrostQL protect sensitive columns and
reveal them only to authorized roles, masking or redacting the value for everyone
else. This page covers the design and the key hierarchy behind it: how a value is
encrypted on write, how it is decrypted or masked on read, and how an equality
search still works through a blind index.

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

An encrypted column may be **selected** for output (it is decrypted or masked as
above) but the ciphertext itself is never usable as a predicate. A
non-deterministic ciphertext used in a WHERE clause would be either useless or an
information oracle — a filter that changes the result set leaks whether a guessed
value matches. Sorts and aggregates on an encrypted column are therefore rejected,
and so is any filter that a blind index cannot serve.

## Searching encrypted columns

Equality search runs through a **blind index**: a sibling column holding a
deterministic keyed hash (HMAC-SHA-256 under the blind-index key) of the
plaintext. The hash is one-way, so the sibling column never exposes plaintext.

Query-side routing is in place. When a filter names an encrypted column that
declares a `blind-index` sibling, BifrostQL rewrites the predicate onto the
sibling column before the query runs, hashing each supplied value with the same
derivation used on write. The rewrite walks nested `and` / `or` groups and joined
single-link filters, so a blind-index equality works wherever a normal filter does.

Two operators are routed:

| Operator | Behavior |
|---|---|
| `_eq` (non-null value) | Rewritten to `_eq` on the blind-index column. |
| `_in` | Rewritten to `_in` on the blind-index column, one token per value. |

Everything else is refused. `_contains`, ranges, `_eq: null`, sorts, aggregates,
an encrypted column with no `blind-index` sibling, and a configuration whose key
manager or `key-ref` is missing all fail closed with an `ACCESS_DENIED` error
rather than falling through to the raw column. A blind index answers "is this
value present", never "which values are near it".

For the operational side — rotating the data key and re-encrypting live rows —
see [rotating field-encryption keys](/BifrostQL/guides/field-encryption/).

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
- **DEK rotation** re-encrypts the affected columns (read with the old DEK, write
  with the new). The versioned ciphertext envelope carries its key version, so old
  and new DEKs serve reads side by side for the length of the rollout.

For the rotation procedure and the online re-encryption sweep, see
[rotating field-encryption keys](/BifrostQL/guides/field-encryption/).
