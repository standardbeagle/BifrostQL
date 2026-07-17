---
written_at: 2026-07-17T08:25:00Z
source_event: task:01KXM3Z1S6NH8AYW36XCGFKPXC
module: bifrostql
category: security,api-compatibility,ci-flakiness
confidence: high
sources:
  - task:01KXM3Z1S6NH8AYW36XCGFKPXC
  - git:61934ea
  - git:37eb346
  - git:09c732b
  - git:ece7c03
  - git:e65e0f2
tags: [protocol-adapter, sigv4, presigned-url, additive-wiring, public-api, no-build-gate, rework]
status: steering
recurrence: 1
---

# S3 slice 2: additive-only wiring, presigned-write gate, and a stale-binary false-fail (1 rewind)

**Lesson.** One review rewind on attempt 1 caught two real defects that a
green build + full test pass did not surface, plus a separate process defect
(stale `--no-build` binaries) produced a false test failure inside the same
task. Three distinct, durable lessons.

## (a) Additive-only wiring — never repurpose an existing public method's body

Commit 61934ea's `AddS3Endpoint` wiring replaced `BifrostSetupOptions
.AddProfile(BifrostProfile)`'s signature/xml-doc/body in place instead of
being inserted alongside it — silently deleting a shipped public API. No
compile error surfaced it (nothing in-repo called `AddProfile` yet); it
would only break external hosts. Caught by review reasoning over the diff,
not by any test.

**Rule.** When wiring a new opt-in surface into an existing options/registrar
class, the diff must be **additive**: new method, new file, or new region —
never overwrite/repurpose an existing public method's signature or body to
"make room." Grep the class's public surface before and after the change and
diff the method list, not just the file.

## (b) Presigned/token auth must gate the allowed verb before signature work

`S3SigV4Verifier.ParsePresigned` authenticated a validly-signed presigned
PUT/POST/DELETE — presigned writes were an explicit task non-goal, but
nothing checked `request.Method`. Today's data path being unimplemented
(clean 501) masked it; the moment a future slice adds `PutObject`, this
becomes a live fail-open. Fixed by gating to GET/HEAD at the top of the
presigned parse path, before any signature computation (commit 09c732b).

**Rule — candidate for promotion.** Any presigned-URL / capability-token auth
path must enforce its allowed-operation set (verb, scope, action) as the
**first** check, before signature/canonical-request verification, not as a
downstream 501/permission check. An unimplemented data path is not a
substitute for the auth contract being correct now — the auth surface is
what future slices build on, and by the time the data path lands, the
"latent" fail-open is live with no auth-layer change needed to trigger it.
This generalizes invariant 7's "adapter builds no predicate, pipeline
decides semantics" pattern to token-scoped operations. **Recommend promoting
to `.claude/rules/protocol-adapter-security.md` as invariant 9** — it is the
same class of finding (pgwire/RESP slices 1-2, this S3 slice), on the same
file, same review gate, and the existing doc already numbers exactly this
kind of adapter-security invariant.

## (c) Revert-the-fix verification must rebuild before the next `--no-build` gate read

During the rework, the implementer reverted a fix to confirm the new test
failed (the review-technique this repo's rules already recommend — see
invariant 8's fixture-rule note), then re-applied it, but did not rebuild
before the next workflow test step ran with `--no-build`. That step
(`server-tests`, attempt 1) read stale binaries and failed
(`Disabled_access_key_is_rejected_indistinguishably` — no exception thrown)
even though the source was already correct; attempt 2, unchanged except for
an implicit rebuild, passed immediately.

**Rule.** Any `--no-build` test-gate step in a workflow assumes the binaries
on disk match the working tree. A revert-and-restore verification cycle
(intentionally reverting a fix to prove a test fails, then reapplying it)
invalidates that assumption exactly once, at the reapply point — an explicit
`dotnet build` (or equivalent) must run before the next `--no-build` gate,
not be left to happen incidentally on a later attempt. Treat this as a step
in the revert-the-fix technique itself, not a separate manual reminder.

## Fixture rule (recurring pattern, not new here)

Not a new instance in this task, but worth noting: the `Disabled_access_key`
test that caught the stale-binary issue is the same shape of targeted,
single-assertion test invariant 8's fixture rule argues for — it isolated
the fail immediately rather than being buried in a broad suite run.
