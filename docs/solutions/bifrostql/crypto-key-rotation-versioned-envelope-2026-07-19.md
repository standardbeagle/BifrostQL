---
written_at: 2026-07-19T17:08:24Z
source_event: task:01KWXANW4DEXBR2B6FN4RZK9A6
source_commits: [87df0cf, f417dd0, 400b3ec, ce9e23e]
form: fact
---

# Crypto key rotation: versioned envelope + why it's safe

**Task**: Crypto key rotation + re-encryption, tests, docs
(01KWXANW4DEXBR2B6FN4RZK9A6). Security review PASS, 0 blockers, clean run
(no rewinds).

## The durable lesson

A versioned ciphertext envelope for AEAD-encrypted fields is safe to
introduce as a **non-breaking, version-directed** migration, and the reason
is structural, not just careful coding: **GCM's authentication tag binds
ciphertext to the exact DEK it was encrypted under.** Decrypting with the
wrong key version is not a silent-success risk — it can only fail
authentication. This is what makes the whole scheme sound:

- Envelope format 1 (legacy, no embedded key version) and format 2
  (`[2][keyVersion:4LE][nonce][tag][cipher]`) coexist; the reader never
  trial-decrypts across candidate keys — it resolves the exact version
  first (`PeekKeyVersion`), then does one directed decrypt. If resolution
  ever picked the wrong DEK, GCM auth fails closed (redact), not
  mis-decrypt.
- Legacy unversioned ciphertext maps to a reserved **version 1 == the
  original unversioned DEK slot** — zero-break migration, no re-encryption
  required to keep old data readable.
- The blind-index key stays HKDF-bound to v1 specifically, so equality
  search continues to work across rotation without a separate index
  rebuild.
- The online re-encryption sweep (`CryptoReEncryptionSweep`) that migrates
  values to the current version follows the existing adapter-write
  invariants unmodified: routes exclusively through
  `IMutationIntentExecutor`/`TableMutationPipeline`, builds no predicate,
  counts `AffectedRows` (never `.Value` — see rule
  `protocol-adapter-security.md` invariant 8b), and is idempotent.

**Generalize**: when adding a versioned/rotatable-key envelope to any
AEAD-encrypted field, the safety argument to make explicit in review is
"wrong key version -> auth failure, never silent wrong-plaintext" — verify
this holds (i.e., confirm the scheme is authenticated, not just encrypted)
before treating version-directed (non-trial) decryption as sufficient.

## Secondary advisory (non-blocking, worth watching)

Review flagged (not a blocker, not caller-reachable under normal writes):
`KeyManagement.GetDataKey(keyRef, version)` on the **read** path, given a
version number no write ever minted, falls through to `ResolveDek`, which
**generates and persists a fresh DEK slot** rather than rejecting the
unknown version — a write side effect hiding on a read path. Harmless only
because the write path always stamps a version that exists and the read
projector still fails closed on GCM auth failure regardless. Not yet
promoted to a rule (single instance); flag if a similar
"read-path-resolves-into-a-minting-function" shape recurs elsewhere in
key/version resolution code.

## Provenance

Source: task 01KWXANW4DEXBR2B6FN4RZK9A6, commits 87df0cf (versioned
envelope), f417dd0 (re-encryption sweep), 400b3ec (cross-dialect binary
round-trip test), ce9e23e (rotation guide doc). Security review verdict:
pass, high confidence, 0 blockers, 3 advisories (stale doc out-of-scope,
the read-path minting gap above, composite-PK fixture hardening
suggestion — none rose to a new invariant on their own).
