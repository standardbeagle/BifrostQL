---
written_at: 2026-07-22T20:05:00Z
source_event: task:01KY4Q2DT5PQ6KW1X4AW6BV8HB
module: bifrostql
category: best-practices
confidence: high
form: constraint
sources:
  - task:01KY4Q2DT5PQ6KW1X4AW6BV8HB#annotation-decision-uniform-FeedAuthException
  - task:01KY4Q2DT5PQ6KW1X4AW6BV8HB#review-passPattern-uniform-failure
  - git:834b98b
tags: [protocol-adapter, security, anti-enumeration, capability-credential, identity-seam, uniform-401, rss]
status: steering
recurrence: 1
promotion_candidate: ".claude/rules/protocol-adapter-security.md invariant 12 — operator decision (first occurrence; distinct axis from invariants 9/10)"
---

# Capability-credential front doors collapse ALL failure conditions to one uniform 401

## Lesson
When the credential IS the capability (a bearer feed token / API key / webhook secret carries authorization by possession), a front door must fail closed to ONE byte-identical status+message for EVERY failure class — missing, malformed, unknown, revoked, expired, table-mismatch, AND unmapped/subjectless principal — because for such a credential existence, validity, and authorization are the same oracle surface; distinguishing them leaks enumeration signal.

## Distinct from invariants 9/10 (do not conflate)
Invariants 9/10 are condition-PRESERVING: map the SAME condition to the SAME wire status across op classes, and keep a 401/403 split so tenant-deny ≠ not-found. The OData and gRPC seams keep that split. A capability-credential surface does the INVERSE — condition-ERASING: it deliberately diverges from the 401/403 split and collapses every distinct condition into a single 401 so that "denied" is indistinguishable from "invalid." Same-condition-consistency (9/10) still holds; on top of it, capability-credential surfaces additionally erase cross-condition distinctions. This is a new axis, not a re-check of 9/10.

## Why it recurs
Every future non-HTTP-auth transport gate that mints a candidate principal from possession-only material faces the same choice, and the natural instinct (copy the OData/gRPC 401-vs-403 template) reintroduces the oracle. The unmapped-issuer case is the trap: it "feels like" a 403 (known token, no principal), but emitting 403 there tells an attacker the token was otherwise valid.

## Apply when
Building or reviewing any capability-token / possession-credential front door: RSS feed endpoints, API-key gates, webhook-secret verifiers — any seam where denied must be indistinguishable from invalid.

## Prevention (structural, not per-branch discipline)
- One private-ctor exception type, one const message, one const 401 (`FeedAuthException.Unauthorized()`) — a second message variant is unrepresentable without a code change.
- Non-vacuous anti-oracle test: collect exceptions from ≥6 distinct failure causes, assert `Distinct()` of BOTH HttpStatus AND Message is a single value. Adding any divergent surface fails the test.
- Downstream endpoint (slice 4) MUST add the exception to its catch filter (invariant 1) and emit a bare 401 with no distinguishing body; must NOT reintroduce a 403 branch for bearer-path unmapped issuers.

## Reusable pass-patterns folded in from this slice
- **Host-store-owned compare.** The authenticator holds no secret material; `IFeedCredentialStore.ResolveAsync(token)` owns lookup+constant-time/anti-enumeration compare (invariant 2 lives in the store), keeping the raw token off the seam and out of logs/cache-keys/ETags. Tradeoff: timing anti-enumeration is now the host store's contractual responsibility — document it as a hard contract on the interface.
- **Identity-only projection overload.** Project the candidate through `IBifrostAuthContextFactory.CreateUserContext(context)` (identity-only), NEVER the merge overload; validate credential (enabled/unexpired/table-in-allow-list) BEFORE projecting. Claim-injection resistance becomes a property of the seam (BifrostContext reads only `context.User` claims + the DI claim-mapper registry), not a convention the endpoint must remember. "Mint no user context on failure" requires no projection on any failed check.
- **Bisectable RED for a new public type.** Ship the RED commit as a compiling `NotImplementedException` stub PLUS the full failing suite (16 tests here), so RED evidence is genuine (tests run and fail) while every commit still builds — satisfies both revert-proven-non-vacuous and every-commit-compiles, which a tests-only RED against a nonexistent type cannot.

## Forward-carried advisories (slice 4 inherits; not separate lessons)
Recorded on the review verdict, already carried into slice-4 context: (1) `ProjectCandidate` mutates `HttpContext.User` to the candidate and does not restore it on projection failure — residual authenticated identity after the uniform 401; restore prior principal in a `finally`. (2) broad `catch (Exception)` around store-resolve/projection swallows `OperationCanceledException` → add `when (ex is not OperationCanceledException)`. (3) `?token=` query credential lands in access/proxy logs + Referrer — slice-4 endpoint should emit `Cache-Control: no-store` + strict Referrer-Policy and docs should prefer the Bearer header form.
