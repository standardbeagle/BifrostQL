---
written_at: 2026-07-20T17:20:00Z
source_event: task:01KXZNNSCNJ4TAZECPYFPZ82SA
module: bifrostql
category: test-failures
confidence: high
form: procedure
sources:
  - task:01KXZNNSCNJ4TAZECPYFPZ82SA#comment-01KXZSHXRWKX51TWTHVVKMY902
  - workflow:01KXZNNSCNB8KSYHY7QK584N52#attempt-01KY07JGHXD1Z4V6JBYAXM25NB
  - git:0b182fdbabc022b1cc1c90c113150acc373a102f
tags: [rewind, missing-criteria, sqlite, mutation-pipeline, boundary-tests]
status: steering
recurrence: 1
---

# Deferred undo requires real-boundary coverage

## Lesson

When a mutation feature's acceptance criteria name pipeline scoping, persistence, or GraphQL wiring, test the scenarios through the real SQLite-backed mutation pipeline and dispatcher; executor-unit tests and recording stubs cannot satisfy that contract.

## What didn't work

The first implementation passed the full Core suite and focused restore tests, but its undo test replaced `IMutationIntentExecutor` with a recording stub. Review found both an unproved acceptance matrix and a real semantic defect: delete restoration could not be represented by an ordinary update because hard-deleted rows are absent and soft-deleted rows are excluded by normal update scope.

## Why it recurs

Local tests can prove intent construction and affected-row handling while skipping the composition points where tenant filters, soft-delete behavior, concurrency transformers, persistence state, idempotence, and schema/dispatcher wiring interact. A green broad suite does not prove a newly required boundary when no test crosses it.

## Apply when

Apply to undo/replay, approval replay, deferred writes, and other mutation orchestration whose contract depends on transformer composition or externally exposed mutation wiring.

## Prevention

Before review, map each boundary-bearing acceptance scenario to a non-stub SQLite test. Exercise full success, drift/no-row conflict, partial outcome, tenant denial through transformer narrowing, insert re-edit protection, delete restoration, idempotent retry, and the public GraphQL path. Keep focused unit tests for construction details, but do not count them as integration evidence.
