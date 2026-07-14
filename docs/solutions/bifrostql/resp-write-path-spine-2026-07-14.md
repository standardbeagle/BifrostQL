---
written_at: 2026-07-14T00:00:00Z
source_event: task:01KXEBC2FVZ1K7AK38SMPRZKNK
module: bifrostql
category: best-practices
confidence: high
sources:
  - task:01KXEBC2FVZ1K7AK38SMPRZKNK
  - git:9636c60
  - git:148bc46
tags: [protocol-adapter, write-path, mutation-pipeline, off-by-default, security-invariant]
status: steering
recurrence: 1
---

# Adapter write path spine (first write slice, RESP epic)

**Lesson.** RESP slice-5 is the epic's first write path (SET/HSET/DEL). Clean
run, no rewind, review PASS + 3 non-actionable advisories — the value here is
the pattern it establishes, generalized into `.claude/rules/protocol-adapter-security.md`
as invariant 7 (below), not a defect fix.

**What didn't work.** N/A — clean run. Captured because it's the FIRST write
path in the epic (prior slices 1-4 were read/codec-only); the pattern it sets
will be copied by every subsequent write-capable adapter feature, so it's
worth locking in now while it's fresh rather than after a divergent copy ships.

**Why it recurs.** Every future adapter write surface (pgwire writes, any
new RESP command, any other `IProtocolAdapter`) faces the same three
temptations: build a WHERE clause adapter-side "just for this narrow case",
special-case soft-delete because "the adapter already knows it's a delete",
or ship the dangerous capability defaulted on because "it's gated by a flag
somewhere anyway."

**Apply when.** Adding any write-capable command/handler to a protocol
adapter (`IProtocolAdapter` implementation).

**Prevention.** Registered as invariant 7 in
`.claude/rules/protocol-adapter-security.md` (see below) — check it before
building any new adapter write path.

## Recurring pattern

1. Adapter write path spine: `IMutationIntentExecutor` exclusively, no adapter-built predicate, delete routes an intent not a hard-delete decision.
2. Off-by-default dangerous-capability gate checked FIRST, before any parse/model/executor work — disabled surface can't even be probed.
