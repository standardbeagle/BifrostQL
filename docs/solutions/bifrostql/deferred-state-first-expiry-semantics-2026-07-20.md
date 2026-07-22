---
written_at: 2026-07-20T18:15:22Z
source_event: task:01KWXBS64S4H50GB7SHPTCA7KY
module: bifrostql
category: logic-errors
confidence: high
form: constraint
sources:
  - workflow:01KY08CXZR81PECFM4J9NP18MZ#attempt-01KY09DWWYMQ6EMPRAMVS6PGQ6
  - task:01KWXBS64S4H50GB7SHPTCA7KY#comment-01KY0A0KT0KAGESPFSK24NWCQV
  - git:50abdbcb206127cc9883978d2abeecf79cc0bbb2
tags: [rewind, state-machine, expiry, resumability, compensating-events, deterministic-clock]
status: steering
recurrence: 1
---

# Deferred lifecycle state must precede expiry checks

## Lesson

For durable time-window workflows, interpret the persisted lifecycle state before applying wall-clock expiry: expiry closes only an unclaimed initial state, while claimed work must remain resumable and released work must remain compensatable.

## What didn't work

The first implementation committed an `undoing` claim but made retries exclude that state, so interruption could strand the change set and its held events. The first repair made `undoing` resumable, yet a top-level expiry check still rejected both a post-expiry resume and the real `released` → dispatched → compensating-undo path. Its test hid the latter by advancing the release engine with a future clock while undo read the system clock.

## Why it recurs

An undo-window check looks like a universal entry precondition, but durable state machines change the meaning of time after a successful conditional transition. Separate clocks in cooperating components can also manufacture lifecycle states that production can never observe together.

## Apply when

Apply to deferred undo, approval expiry, leases, schedulers, outbox release, compensation, and any workflow that persists an in-progress or externally-visible state across process interruption.

## Prevention

Branch on persisted state first. Apply the expiry predicate only while atomically claiming the initial state; allow durable in-progress states to resume and externally released states to enter compensation. Inject one clock through every participant in deterministic lifecycle tests, and exercise the real transition chain rather than manually seeding an intermediate state.
