---
written_at: 2026-07-14T11:00:00Z
source_event: task:01KXEBBPR2MBVK2N1R6WYK517M
module: bifrostql
category: security
confidence: high
sources:
  - task:01KXEBBPR2MBVK2N1R6WYK517M
  - git:d32d7c5562f545732a6756343ce491bc91420a0d
tags: [resp, protocol-adapter, wire-decode, uncatchable-crash, stack-overflow, dos, rewind, recursive-decoder, playbook-reuse]
status: steering
recurrence: 1
---

# resp slice 1: RESP2/RESP3 codec + auth — durable lessons

## Lesson 1 — a recursive wire decoder needs a nesting-depth cap BEFORE recursing; width/size caps do not cover it (THE REWIND)

`RespReader`'s aggregate/map decoder (`ReadAggregateAsync`/`ReadMapAsync` →
`ReadValueAsync`) had element-count (`_maxElements`) and bulk-length
(`_maxBulkLength`) caps but **no depth cap**. An unauthenticated peer sending
a few KB of nested array headers (`*1\r\n` repeated N times) recurses one
physical stack frame per level. Once the socket has data buffered, the
inner `ReadAsync`/`ReadRawByteAsync` awaits complete synchronously, so the
*physical* call stack — not just an async continuation chain — grows
unbounded, producing a `StackOverflowException`. That exception is
**uncatchable in .NET**: it escaped both the
`RespProtocolException/FormatException/OverflowException/ArgumentException`
catch and the `IOException/OperationCanceledException` catch, and would
have crashed the entire host process — HTTP + pgwire + RESP front doors
together, not just the offending connection. Caught by correctness-review
attempt 1 (blocker, rewind to build step), fixed in d32d7c5 by adding
`RespWireOptions.MaxNestingDepth` (default 32) threaded as a depth counter
through the recursive read path, checked **before** each descent (guard
prevents the stack from ever growing past the cap — it does not merely
detect after the fact). Depth resets to 0 only at each top-level frame in
`ReadValueAsync`; the fix specifically avoided routing children back
through `ReadValueAsync(depth:0)`, which would have silently reset the
counter and defeated the guard — children now go through a dedicated
`ReadRequiredValueAsync` that inlines the raw-byte read + `ReadBodyAsync(depth)`.
Over-deep aggregates raise `RespProtocolException` (the caught base) on the
unauthenticated path → clean protocol error, connection closes, no crash.

**Why it recurs / generalizes**: this is a distinct defect class from the
pgwire drain's invariant 5 (catch the full parse-exception family) —
invariant 5 is about exceptions that *can* be caught after the fact; a
`StackOverflowException` can only be *prevented*, never handled, by any
catch clause at any layer. Any recursive wire/untrusted-input decoder
(frame trees, nested aggregates, nested expressions) that only bounds
*width* (element count) and *size* (byte length) is still exposed to this
DoS if it doesn't also bound *depth* before recursing.

**Apply when**: any `IProtocolAdapter` (or future non-GraphQL front door)
implements a recursive decoder over untrusted bytes on the pre-auth path.

**Prevention**: add a `MaxNestingDepth`-style cap, thread depth as a plain
parameter (not a field that could leak across concurrent calls), check
and throw the adapter's own protocol exception **before** recursing, and
reset depth only at genuine top-level frame boundaries. Promoted to
`.claude/rules/protocol-adapter-security.md` as invariant 6.

## Lesson 2 — allocation amplification from an attacker-controlled length prefix (folded into the same fix)

Aggregate/map readers pre-allocated `new RespValue[count]` /
`new KeyValuePair[count]` from a declared count before reading any element
— a ~13-byte lying prefix (`*1000000\r\n`) forced a multi-MB allocation
before validating a single element arrived. Fixed alongside the depth cap
by growing a `List` incrementally; a truncated/lying stream now throws on
EOF having materialized only the elements that actually arrived.

**Apply when**: any decoder pre-sizes a collection from a wire-declared
count on the unauthenticated path.

**Prevention**: grow incrementally (`List.Add`), never `new T[declaredCount]`
from an unvalidated prefix.

## Note — pgwire epic playbook reuse confirmed

RESP slice 1 is the first slice of the second protocol epic and reused the
pgwire epic playbook end-to-end without adaptation: `IProtocolAdapter` on a
Kestrel `ConnectionHandler` + `IHostedService` (`RespWireAdapter`/
`BifrostRespExtensions` mirror `PgWireAdapter`/`BifrostPgwireExtensions`),
the shared `DuplexPipeStream` (not duplicated), fail-closed AUTH via
`IBifrostAuthContextFactory` with the pgwire slice-1 timing-safe
compare-before-null-check pattern, an `IRespCredentialStore` seam mirroring
`IPgCredentialStore`, and a land-the-seam `IRespCommandHandler` dispatch
table for later slices. The playbook is validated as reusable across a
second wire protocol, not just internally consistent within pgwire.

This is also the **second rewind on the drain overall**, and both rewinds
were the same shape: a fail-open/uncatchable-crash DoS-class defect on the
unauthenticated decode path, caught by the automated correctness-review
gate before merge, not by unit tests. Reinforces that adversarial review on
the codec/decode path is disproportionately high-value for this codebase.
