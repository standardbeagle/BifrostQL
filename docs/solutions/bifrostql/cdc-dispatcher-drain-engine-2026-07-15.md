---
written_at: 2026-07-15T00:00:00Z
source_event: task:01KXGYVV9ENDJ36V52H1GQ59G8
module: bifrostql
category: best-practices
confidence: high
sources:
  - task:01KXGYVV9ENDJ36V52H1GQ59G8
  - git:9c28465
  - git:aead0a1
  - git:c62715b
  - git:816cd89
tags: [cdc, outbox-dispatcher, hosted-service, core-no-hosting-dependency, testability, dead-letter]
status: steering
recurrence: 1
---

# CDC outbox dispatcher drain engine (slice 4a)

**Lesson.** Clean run, no rewinds — all four workflow steps (scope-check,
build, test, correctness-review) passed on the first attempt. The reviewer
verdict was PASS with two advisories that are captured here as forward-facing
constraints for slice 4b, not defects fixed in 4a.

## Core-has-no-hosting-dependency pattern (reusable)

`OutboxDispatcher` (`src/BifrostQL.Core/Modules/Cdc/OutboxDispatcher.cs`) is
the drain ENGINE and lives entirely in `BifrostQL.Core` — it takes no
dependency on `Microsoft.Extensions.Hosting`. `RunAsync(CancellationToken)`
is the polling loop; `BifrostServiceRegistrar.cs` in `BifrostQL.Server` adds
a thin `CdcOutboxHostedService : IHostedService` wrapper that launches
`RunAsync` detached on `StartAsync` (so a slow first poll never blocks host
startup) and cancels + drains on `StopAsync`. This mirrors the existing
`IProtocolAdapter` / `ProtocolAdapterHostedService` split (see AGENTS.md
Request Flow). **Apply this same split to any future Core background
worker**: engine + `RunAsync(CancellationToken)` in Core, `IHostedService`
wrapper only in `BifrostQL.Server`.

Both single- and multi-database hosts register the wrapper
(`BifrostMultiDbOptions.cs`, `BifrostSetupOptions.cs`); the `IEventSink`
dependency is optional so a host without a sink, or without
`MetadataKeys.Cdc.OutboxTable` configured, registers cleanly and idles
without polling — a non-CDC host pays nothing.

## Testability split (reusable)

Two extraction points make the engine deterministically testable without
racing a real timer or wall clock:

- `DrainOnceAsync(...)` (internal static) does exactly one poll-and-deliver
  pass and returns a `DrainOutcome` — tests call it directly instead of
  running the full `RunAsync` loop.
- `ComputeBackoff(int attempts, double jitter, TimeSpan baseDelay, TimeSpan
  maxDelay)` (internal static) is a pure function; `RunAsync` supplies jitter
  via an injected delegate (`_jitter()`), so backoff growth, the `maxDelay`
  ceiling, and the non-zero floor are pinned by a plain unit test
  (`tests/BifrostQL.Core.Test/OutboxBackoffTests.cs`) with no timer
  involved.

**Apply this same split to any future poll-loop/backoff worker**: separate
"one pass" from "the loop", and make backoff a pure function with injected
randomness.

## Interim limitation on record: head-of-line starvation until 4b

`DrainOnceAsync` treats every per-row exception (unknown aggregate,
malformed payload, missing key column in `ComputeSubject` or
`CloudEventEnvelope.Build`) as `TransientFailure` and stops the pass at the
first failure, to preserve monotonic delivery order. A permanently-poison
row (not a transiently-down sink) is therefore indistinguishable from a
transient one in 4a: it is never marked `dead`, stays `dispatched_at IS
NULL`, is re-read first every pass, and starves every higher-id healthy row
indefinitely while its `attempts` counter grows unbounded. The
pure-transient case (sink temporarily down) does not starve — ordering
resumes cleanly on recovery once the sink recovers, per
`StopsAtFirstTransientFailure_PreservingOrder`.

This is **acceptable only because it is explicitly interim**: slice 4b
(dead-letter, per-PK ordering, idempotency key) is a REQUIRED follow-up, not
optional polish — reviewer flagged it as a hard blocker on treating 4a as
production-ready in isolation. 4b must add a cap that converts a
permanently-poison row to `dead=true` (or an equivalent skip-ahead) so
healthy rows are never starved behind one bad row.

## Coverage gap to close before building on top of 4a

`tests/BifrostQL.Core.Test/Integration/Sqlite/OutboxDispatcherTests.cs`
proves `Delivered`/`TransientFailure` stamping and the stop-at-first-failure
ordering, but does not directly test:

1. sink-**throws** → caught, logged, converted to `TransientFailure`
   (`attempts++`, `dispatched_at` stays null) — the fail-safe contract is
   currently reviewed-correct by inspection only;
2. `CancellationToken` honoured mid-drain;
3. the no-op-host stop path (a host with no sink/no outbox table configured
   idles and shuts down cleanly).

4b should add a throwing `FakeSink` and a cancelled-token drain test to lock
these before extending the dispatcher further.
