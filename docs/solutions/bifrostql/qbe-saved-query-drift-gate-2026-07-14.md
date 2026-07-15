---
written_at: 2026-07-14T20:30:00Z
source_event: task:01KWXEA35B59Q9E0J4698SB694
module: bifrostql
category: security
confidence: high
sources:
  - task:01KWXEA35B59Q9E0J4698SB694
  - git:00ffcac
  - git:e02ef33
  - git:5f0f2f4
tags: [qbe-designer, saved-queries, schema-drift, degraded-mode, untrusted-json, vacuous-test, review-correction]
status: steering
recurrence: 1
---

# QBE saved-query drift gate: persisted fingerprint can lie, degraded mode must block writes

**Lesson.** Task 01KWXEA35B59Q9E0J4698SB694 ("2.1 Saved queries from QBE
designer") rewound on review attempt 1 with 6 blockers (2 critical, 4 major);
attempt 2 fixed all and passed. Base d4036d6, impl commits e892166/3858e97/
93640ed/a2c6b34, rework commits 00ffcac/e02ef33/5f0f2f4. The blockers share
a small number of through-lines worth generalizing to the next slice that
persists client state and re-derives a safety gate from it.

## 1. A safety gate must be derived from live state, not a persisted summary

The degraded-mode gate (disables Run when a saved query references a
dropped table/column) read `definition.fingerprint` — a snapshot written at
save time — instead of recomputing drift from the restored designer state.
A stored fingerprint that is empty, stale, or truncated silently opened the
query with `degraded = false`, defeating the gate the slice exists to
provide. Fixed by deriving drift from `queryRefs(state)` inside
`detectSchemaDrift`, and the persisted fingerprint field was **removed
entirely** rather than kept as a second source of truth — `serializeQuery`
is deterministic, so a persisted copy of its output carries no information
the state lacks and can only disagree with it.

**Apply when:** any feature persists a derived summary/fingerprint of state
alongside the state itself and later uses that summary to gate a safety or
security decision (degraded mode, staleness, drift, integrity checks).
**Prevention:** if the derivation is a pure function of already-persisted
state, recompute it at the gate and don't persist the summary at all. If it
must be persisted (e.g., too expensive to recompute), the gate must
validate the summary against the state, never trust it standalone.

## 2. Enumerate the WRITE actions first when disabling a degraded mode

Attempt 1 found Run (a read action) already gated by degraded mode, but
Save and Save-as (write actions) were not — the one action that durably
harms the store (writing a drifted definition back with an unrepaired
fingerprint) was the one left enabled. The read action is the obvious one
to gate and gets built first by default; the write action is the one that
causes lasting damage and is easy to omit.

**Apply when:** any feature enters a degraded/safe/read-only mode that
should restrict user actions. **Prevention:** when listing actions to gate,
list writes (save, delete, mutate, persist) before reads (view, run,
export) — the write list is the one that must be complete.

## 3. Untrusted JSON needs element-level validation, not just container-level

`parseQueryDefinition` checked `Array.isArray` on `tables`/`columns`/`joins`
but never validated individual elements. A definition with a null table or
a column missing its `criteria` array passed parsing, reached `setState`,
and threw a `TypeError` out of a render-time `useMemo` — with no
`ErrorBoundary` in the frontend, this blank-screens the pane instead of
showing the intended "not a saved visual query this version can open"
error. Fixed by validating tables/columns/joins/filter-tree element by
element and returning `null` (→ the clean error banner) on the first bad
shape.

**Apply when:** parsing any JSON that crosses a trust boundary (server
storage, file import, another process) into state consumed by a
render-time memo or effect. **Prevention:** validate every element a
downstream memo/effect will index into or dereference, not just the
container type; reject with a clean error path before `setState`.

## 4. A test that asserts a pure function's structural incapability is vacuous

The "never rewrites the definition it inspects" test asserted
`detectSchemaDrift` doesn't mutate its argument — but the function's body
is two `.filter()` calls, so it is structurally incapable of mutating
anything; the test could not fail regardless of what the persistence path
did. The actual non-destructive invariant lives on the persistence path
(`onRename`/`onSave` in `QueryBuilderPane.tsx`), which was untested. Fixed
by deleting the vacuous test and adding component tests over the
persistence path (rename writes back the same id and an unchanged stored
definition; delete only calls `remove` when confirmed).

**Apply when:** writing a test for a "doesn't mutate / doesn't rewrite /
doesn't leak" invariant. **Prevention:** ask whether the function under
test could structurally violate the invariant at all; if not, move the
assertion to the call site (persistence, network, storage layer) where a
future change could actually introduce the violation.

## 5. Review-finding hygiene: an overstated finding gets corrected on the record

One attempt-1 blocker claimed `collectFilterRefs`/`state.filter` produced a
drift false negative (a dropped column referenced only from a nested filter
tree wouldn't be caught). The rework investigated and found
`serializeQuery` already folded `collectFilterRefs` into the fingerprint at
commit a2c6b34 — the path was untested, not broken. Attempt 2's reviewer
verdict explicitly recorded "blocker-7 correction is CORRECT ... Attempt-1's
finding overstated the defect: that path was untested, not broken; no false
negative existed" rather than silently absorbing the fix into a generic
"added tests" note.

**Apply when:** a rework addresses a rewind finding that turns out to be
partially or fully incorrect on investigation. **Prevention:** state the
correction explicitly in the fix commit or the next review pass — don't let
an inaccurate finding stand uncorrected in the task history, and don't let
the correction get silently absorbed as if the defect were real.

## Dropped candidate

A sixth candidate — "one-shot open requests to survive pane unmount/remount"
— was a real attempt-1 blocker (stale `openRequest` replaying on remount
and resurrecting a deleted query) but is UI-lifecycle-specific to this
pane's remount-on-tab-switch pattern, not a generalizable defect class like
the five above. Not included as a numbered lesson; see the task's added
acceptance criteria for the concrete fix if this pattern recurs elsewhere.
