---
written_at: 2026-07-15T00:00:00Z
source_event: task:01KXEBCVVCCJCQ39EY0PPZ8DPV
module: bifrostql
category: correctness
confidence: high
sources:
  - task:01KXEBCVVCCJCQ39EY0PPZ8DPV
  - git:ffcfa22
  - git:37814d7
tags: [cdc, webhook-sink, hmac, secret-rotation, fail-closed, error-mapping, review-clean-run]
status: steering
recurrence: 1
---

# CDC webhook sink: sign/rotate/fail-closed discipline (slice 5)

**Lesson.** Clean run — 4/4 workflow steps passed on attempt 1, no rewind.
The signal here is not a caught defect but a set of non-obvious constraints
the acceptance criteria demanded and the implementation (`ffcfa22`/`37814d7`,
`src/BifrostQL.Core/Modules/Cdc/WebhookEventSink.cs`) got right the first
time — worth pinning so the next sink built against `IEventSink` copies the
same shape instead of re-deriving it.

## Sign the same byte[] you send — never re-serialize between sign and send

`WebhookEventSink.cs:93` serializes the envelope to `byte[] body` exactly
once; that same `body` variable is both HMAC-signed (line 95) and wrapped in
`ByteArrayContent` for the wire (line 107/109). A receiver verifies the
signature against the literal bytes it received; if the sink re-serializes
(even semantically-identical JSON — key order, whitespace, culture-formatted
numbers can all differ) between computing the signature and building the
request body, the signature silently stops matching on the receiving end
with no exception on the sending side. Any signer of an outbound payload
must thread one serialized byte array through both the sign step and the
send step — never call `ToJsonString()`/serialize twice.

## Rotation: comma-separated secret list, one signature header value per active secret

`ParseSecrets` splits `webhook-secret` on `,` (`TrimEntries |
RemoveEmptyEntries`); `ComputeSignatures` loops every parsed secret and
`DeliverAsync` emits one `X-Bifrost-Signature` header value per digest
(multi-valued header, not comma-joined signatures). This is what makes
rotation zero-downtime: during the overlap window the metadata value is
`"old,new"`, the sender emits both signatures, and the receiver — checking
against whichever secret it currently trusts — always finds one that
verifies. A rotation scheme that signs with only the newest secret has a
window where sender and receiver disagree; a scheme that requires
coordinated restart defeats the point of hot rotation. Any credential
rotation over a metadata-driven secret should default to "sign/attempt with
every currently-active credential," not "assume the latest."

## Fail-closed on empty/whitespace secret: prove `WasCalled == false`, not just a good return code

When `ComputeSignatures` produces zero signatures (secret unset, or only
whitespace after trim), `DeliverAsync` returns `TransientFailure` **before**
constructing any `HttpRequestMessage`. The test that proves this
(`With_no_active_secret_it_refuses_to_send_and_reports_transient_failure`)
doesn't just assert the return value — it asserts the fake HTTP handler's
`WasCalled` is `false`. A test that only checks the return code would still
pass if the code sent an *unsigned* request and then separately decided to
report failure; asserting the transport was never invoked is what actually
proves "refuses to send," not just "reports refusal." Any fail-closed path
guarding a network/side-effecting call needs a substitute/fake that can
prove non-invocation, not just a return-value assertion.

## Retry/backoff stays in the dispatcher; the sink returns an outcome, once

`WebhookEventSink.SendAsync` maps every outcome (success, transport
exception, non-2xx) to `Delivered`/`TransientFailure` exactly once and
returns — no loop, no `Task.Delay`. `BaseBackoff`/`MaxBackoff`/
`ComputeBackoff` live only in `OutboxDispatcher.cs` (slice 4a/4b). This
mirrors the CDC delivery-guarantees lesson from slice 4b (at-least-once is a
dispatcher property) and the protocol-adapter pattern of thin wire
components: a new `IEventSink` implementation should never grow its own
retry loop — that duplicates dispatcher policy and produces two different
backoff behaviors depending on which sink is active.

## Error-mapping seam: test with a throwing substitute, and discriminate cancellation from transient failure

`ThrowingHandler : HttpMessageHandler` (test-only) throws a supplied
exception from `SendAsync`, used to prove two different outcomes at the same
catch seam (`WebhookEventSink.cs:135-145`): an `HttpRequestException` maps to
`TransientFailure` (swallowed, dispatcher will retry), but an
`OperationCanceledException` on a cancelled token propagates rather than
being masqueraded as a transient failure. A catch-all that maps every
exception type to the same outcome would silently convert a caller-requested
cancellation (e.g. host shutdown) into "retry later," which is wrong on two
axes: it hides the real reason and it lets the dispatcher waste a retry
attempt on something that was never going to succeed. Any adapter/sink error
-mapping catch block handling more than one exception family needs a test
per family, built with a throwing fake at the transport boundary, not just a
happy-path test plus one generic-exception test — this is the same
gap-class flagged for adapter write paths in `resp-write-path-spine`, now
confirmed at a dispatcher/sink seam too.
