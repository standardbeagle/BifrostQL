---
written_at: 2026-07-13T23:10:00Z
source_event: task:01KXEBB9EVJ6M621VDBY368NW4
module: bifrostql
category: logic-errors
confidence: high
sources:
  - task:01KXEBB9EVJ6M621VDBY368NW4
  - git:2a31036
tags: [pgwire, protocol-adapter, wire-decode, fail-open, exception-handling, rewind, cancel-request, connection-limiter]
status: steering
recurrence: 1
---

# pgwire slice 5: extended query protocol — durable lessons

## Lesson 1 — a narrow `catch` on a `Parse` call over untrusted wire input is a fail-open connection-teardown bug (THE REWIND)

`PgExtendedQueryProcessor`'s Bind parameter-decode caught only
`FormatException`. `long.Parse`/`decimal.Parse`/`double.Parse` on an
out-of-range (but well-formed) numeric text value throw `OverflowException`,
not `FormatException`. That exception escaped the decode catch, escaped
`HandleAsync`/`RunQueryLoopAsync`, and escaped the connection handler's
filtered catch — reaching Kestrel unhandled and dropping the connection
with **no** `ErrorResponse`. Driver/attacker-triggerable with a single Bind
message. Caught by correctness-review attempt 1 (blocker, rewind to build
step), fixed in 2a31036 by widening the catch to
`FormatException or OverflowException or ArgumentException`, covering every
typed decode path (int2/4/8, float, numeric, uuid, bool, date/timestamp).

**Why it recurs**: this is the THIRD instance of the same defect class on
this pgwire drain — slice-2 forwarded raw `BifrostExecutionError.Message`
to the wire (invariant 3), slice-3 let an overflow escape into an unclean
syntax error, slice-5 let an overflow escape a bind decode entirely past
error handling. The common root: **decoding untrusted wire input with a
narrow catch clause that only matches the "obviously malformed" exception
type, not the full parse-exception family.** `FormatException` alone is
never sufficient: numeric overflow raises `OverflowException`, and GUID/
DateTime parsing can raise `ArgumentException` on edge cases too.

**Apply when**: any protocol adapter (pgwire, any future `IProtocolAdapter`)
decodes a typed value out of untrusted wire bytes.

**Prevention**: catch `FormatException or OverflowException or ArgumentException`
(or the language equivalent) around every `Parse`-family call fed by wire
input, never `FormatException` alone. Promoted to
`.claude/rules/protocol-adapter-security.md` as invariant 5 (see below).

## Lesson 2 — cross-connection control messages: crypto-random (PID, secret) pair, exact-match, fail-silent-closed (reference pattern)

`PgCancellation`'s CancelRequest design, reviewer-cleared SAFE: each
connection gets a `(PID, secret)` pair both drawn from
`RandomNumberGenerator` (not sequential/guessable), published to the client
in `BackendKeyData`. A CancelRequest arriving on a *separate* connection
looks up the target by PID and requires an exact secret match before it
cancels a cooperative, per-query linked `CancellationTokenSource`. Wrong
secret, unknown PID, or a short/malformed payload is a silent no-op that
never touches the target connection. Registration is removed on disconnect
so a reused PID can't be hijacked by a stale entry. The CTS itself is
scoped per-query (reset in `BeginQuery`, disposed in `EndQuery` under a
gate) so there's no cross-query token leak.

**Apply when**: designing any cross-connection control-plane message
(cancel, kill, admin-interrupt) on a stateful protocol adapter.

## Lesson 3 — admission control: lock-free CAS counter, released in `finally` (reference pattern)

`PgConnectionLimiter` uses an `Interlocked`-based CAS counter, acquired
post-startup/pre-auth; over-limit responds with SQLSTATE `53300`
(`too_many_connections`) plus a clean close; the permit is released in a
`finally` block, so it is reclaimed even on the fail-open exception path
this same slice fixed in Lesson 1. Reference pattern for any admission
control on a connection-oriented adapter.

## Note — the drain's first rewind, working as designed

Slices 1-4 of this pgwire drain passed review on the first attempt (with
advisories fixed pre-close). Slice 5 is the first genuine rewind: a real
fail-open bug, caught by the automated review gate before merge, fixed,
and re-reviewed to a clean pass. Recorded not as a process failure but as
confirmation the rewind path (blocker → append criteria → re-implement →
re-review) works as designed — the value of the gate showed up exactly
when a real defect existed.
