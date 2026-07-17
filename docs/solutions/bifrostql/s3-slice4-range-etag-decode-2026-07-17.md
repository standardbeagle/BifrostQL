---
written_at: 2026-07-17T09:30:00Z
source_event: task:01KXM3Z7Z6DRH697PAECXPPF52
module: bifrostql
category: security,api-design
confidence: high
sources:
  - task:01KXM3Z7Z6DRH697PAECXPPF52
  - git:466316e
  - git:d426280
  - git:72bafad
  - git:03e953f
tags: [protocol-adapter, s3, byte-range, etag, conditional-request, overflow, resolved-object]
status: steering
recurrence: 1
---

# S3 slice 4: GetObject/HeadObject ranges+ETags (clean run, 0 blockers)

**Lesson.** Single attempt on every workflow step (scope-check, build, both
test suites, security-review) — no rewind. Review verdict pass, 5 advisories,
all deferred to future slices. Two durable patterns worth generalizing; the
rest is a punch list.

## (a) Content read gated by a type, not a rule — `ResolvedFileObject` as the only mint

`FileObjectSeam.CopyContentToAsync` (466316e) takes a `ResolvedFileObject`,
which only `ResolveAsync` can construct. There is no overload and no code
path that reaches storage/path access from a raw key — the identity-gated
row read (tenant isolation, soft-delete, policy) is structurally
unbypassable, not enforced by callers remembering to check first. `03e953f`
confirms the shape at the adapter: GetObject/HeadObject resolve the row
under the caller's identity BEFORE any storage or path access, and a
missing row, unauthorized row, row holding no object, or malformed address
all collapse to the same `NoSuchKey` 404 (non-enumerating). This is the same
"make the seam type-check the invariant instead of hoping for discipline"
shape as the slice-1 address-vs-storage-key fix (see
`s3-slice1-address-vs-storage-key-2026-07-16.md`) — prefer a type that only
the authorized path can mint over a runtime check that a caller could skip.

## (b) Arithmetic-clamp overflow handling, not a throw path — invariant 5 by construction

`S3ByteRange` (d426280) parses Range-header bounds by digit-scanning and
clamping rather than `long.Parse` + catch: an all-digit bound that overflows
`Int64` is valid-but-too-large, not malformed, so an over-large start is
unsatisfiable (416), an over-large end clamps to the last byte, and an
over-large suffix means the whole object — resolved arithmetically, with no
`OverflowException` ever reaching the wire. This satisfies
`.claude/rules/protocol-adapter-security.md` invariant 5 (catch the full
parse-exception family) by eliminating the throw path entirely rather than
widening a catch clause — a stronger fix where the input shape allows it
(digit strings bounded to a known scalar type), and worth reaching for
before reaching for a wider `catch` on any future wire-input-decode adapter
work.

## Advisories deferred to future slices (not blockers, worth tracking)

1. `S3ConditionalRequest.NormalizeTag` strips the `W/` weak-tag marker for
   both `If-Match` and `If-None-Match`; RFC 7232 requires strong comparison
   for `If-Match` (a weak tag must never match it). Low impact — this
   adapter never emits weak ETags today — but only strip `W/` for
   `If-None-Match` if a future slice adds weak-ETag support.
2. `S3Middleware.WriteUserMetadata` strips control characters (blocks CRLF
   response-splitting) but not non-token header-name chars or non-ASCII
   values; Kestrel rejects those at write time, degrading that object's GET
   to a generic sanitized 500 via the catch-all. No injection or invariant-1
   escape; an ASCII/token allowlist would degrade to header-skip instead of
   500.
3. `IStorageProvider.OpenReadAsync`'s default interface method buffers the
   whole object via `DownloadAsync` (honestly documented). Only
   `LocalStorageProvider` exists today and overrides with a seekable
   `FileStream`, so the no-full-file-buffering criterion holds for every
   live provider — revisit the DIM default when a remote provider lands.
4. `LocalStorageProvider.GetFullFilePath` guards traversal/rooted/prefix
   escape lexically (`Path.GetFullPath` + `EnsureUnderBase`) but does not
   resolve symlinks; the symlink-escape case is untested. Exploitation
   requires host-filesystem write access (S3 writes are 501, upload keys are
   server-generated) so it's out of today's wire threat model — add a
   `File.ResolveLinkTarget` check or documented limitation before any slice
   grants filesystem write access near storage roots.
5. HEAD responses write an XML body on 416/error envelopes (Kestrel discards
   HEAD bodies — harmless); a dangling row `FileKey` (file missing on disk)
   surfaces as generic `InternalError` 500 rather than `NoSuchKey` — this is
   post-visibility only, so the non-enumeration policy is unaffected, but a
   future slice could map "resolved but missing on disk" to `NoSuchKey` for
   a cleaner signal.
