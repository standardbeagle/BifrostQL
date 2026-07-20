---
written_at: 2026-07-20T10:15:00Z
source_event: [task:01KWXBS640D4GT6RR1674697J0, git:34b3085]
module: bifrostql
category: workflow-and-design
confidence: high
form: procedure
sources:
  - task:01KWXBS640D4GT6RR1674697J0#comment-01KXZES7S7YEA8BVPTTMQVHB9R
  - git:34b3085
status: steering
recurrence: 1
---

# Deferred configuration slice lessons

## Lesson
Resolve file scope against the current tree before implementation; for opt-in deferred behavior, validate prerequisites and pin the reversal decision before adding capture code.

## What did not work
The task scope named `src/BifrostQL.Core/Model/MetadataKeys.cs`, but the live contract had moved to `src/BifrostQL.Abstractions/Model/MetadataKeys.cs`; implementation had to stop until scope was corrected. The finished slice deliberately made no write-path change.

## Why it recurs
Shared contract types can move during layering or extraction, while task prose remains stale. A metadata contract is unsafe when an explicit opt-in silently accepts missing concurrency/history prerequisites or invents a default undo window. Capture work can also accidentally encode stage-then-apply versus apply-then-reverse before the system's read/write authority is chosen.

## Apply when
Starting a metadata/module slice, especially after an Abstractions/Core carve-out, or before implementing any capture, replay, undo, or other write behavior.

## Prevention
- Verify every scoped path with the repository tree and update scope before editing; do not create a duplicate type at the stale path.
- Keep the opt-in sentinel explicit, parse only the documented duration grammar, provide no implicit window, and reject missing prerequisites as model-load errors.
- Record an ADR naming the apply-then-reverse or stage-then-apply choice before capture/replay code; keep the contract slice write-neutral until that decision is consumed by a later slice.
