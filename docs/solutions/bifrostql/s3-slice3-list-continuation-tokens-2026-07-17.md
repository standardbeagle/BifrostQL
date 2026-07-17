---
written_at: 2026-07-17T09:05:00Z
source_event: task:01KXM3Z4FXNHX3NN6XH8H63MR4
module: bifrostql
category: security,api-design
confidence: high
sources:
  - task:01KXM3Z4FXNHX3NN6XH8H63MR4
  - git:eb5036b
  - git:6d77070
tags: [protocol-adapter, s3, continuation-token, hmac, introspection-gate, pagination]
status: steering
recurrence: 3
---

# S3 slice 3: ListBuckets/ListObjectsV2 + continuation tokens (clean run, 0 blockers)

**Lesson.** Single review pass, no rewind, 6 advisories deferred to future
slices. Two durable patterns worth generalizing; the rest is a punch list.

## (a) A third confirmation: listing/introspection surfaces reuse the same authz gate

Bucket enumeration (`ListBuckets`) and object listing both route through the
same `PolicyEvaluator.CanAct` / `IsColumnAllowed` gate the query path uses,
via the shared `PolicyIdentity` projection, fail-closed on evaluation fault.
A non-visible/unknown/non-file bucket lists as `NoSuchBucket`, so the surface
is not an existence oracle either. This is the third independent
confirmation of `.claude/rules/protocol-adapter-security.md` invariant 4
(pgwire catalog emulation, then S3 itself in earlier slices, now S3
list/enumerate) — treat it as settled doctrine for any new
introspection/listing endpoint, not something to re-derive per adapter.

## (b) HMAC-bound opaque continuation token — reusable adapter-pagination shape

`S3ContinuationToken` models the cursor the way the RESP slice-4 SCAN cursor
does: token carries **position only** (last emitted sort key); the query
shape it's bound to (bucket, prefix, delimiter, max-keys, identity
fingerprint) is **never transmitted** — it's re-derived from the current
request on replay and the HMAC recomputed over a **length-prefixed**
canonicalization (prevents field-smuggling, e.g. delimiter into prefix).
Comparison is unconditional `FixedTimeEquals`. Mismatch fails closed as the
adapter's own `S3ProtocolException` (in the middleware's caught family).
Containment note carried into the doc: token validity is a tamper/replay
guard only — the read pipeline still ANDs tenant/policy/soft-delete onto
every query regardless of what the token claims. Any future adapter needing
a resumable cursor should copy this shape (RESP SCAN → S3 continuation
token, same pattern twice now) rather than re-inventing pagination-token
security per adapter.

## Advisories deferred to future slices (not blockers, worth tracking)

1. `IdentityFingerprint`'s claim canonicalization is scalar/string-sequence
   only and its list-join is non-injective (`["a,b"]` vs `["a","b"]`
   collide) — contained today because the token carries position only and
   every query is re-scoped from the *current* caller's context, but
   length-prefix the fingerprint fields if this is ever load-bearing.
2. `MaterializeAsync`'s `MaxListMaterialize` cap checks row count **before**
   the prefix filter — fail-closed (stricter than the doc/commit-message
   claim), not fail-open, but the doc should say what the code does.
3. Up to `MaxListMaterialize+1` rows plus all file-column pointers
   materialize in memory per request — authenticated-only and bounded, but
   a resource-exhaustion lever if key-store principals are ever low-trust.
4. `ContinuationTokenSecret` has no minimum-length/entropy check; a short
   configured secret silently weakens the HMAC.
5. `S3ListXml.Escape` covers the five XML entities but not control chars
   (0x00-0x1F); `OwnerId` comes from the `user_id` claim verbatim, so a
   control char there yields an unparseable (not injectable) document.
6. `CanRead`'s bare catch is correctly fail-closed per invariant 4 but would
   also swallow `OperationCanceledException` — inert today (evaluation is
   synchronous), narrow it if the evaluator ever becomes async.
