---
written_at: 2026-07-15T00:00:00Z
source_event: task:01KXGYVVATXK5Z5B2X4HBKKZRN
module: bifrostql
category: correctness
confidence: high
sources:
  - task:01KXGYVVATXK5Z5B2X4HBKKZRN
  - git:58f9fba7bb6a3a8fac9042d5e62bdb3f4fc7ddc4
  - git:b6619e103ceeee512d9d2b423175f88db1c1da24
  - git:a862dc7001a6d0a1c7d0ce3c081f2f5197279ac8
tags: [cdc, outbox-dispatcher, per-key-ordering, dead-letter, idempotency, shared-table, review-rewind, vacuous-test]
status: steering
recurrence: 1
---

# CDC outbox dispatcher delivery guarantees (slice 4b)

**Lesson.** One review rewind. `58f9fba` shipped per-key ordering,
dead-letter, and an idempotency key on top of slice 4a's drain loop; the
Sqlite tests added in `b6619e1` all passed; correctness-review attempt 1
still blocked and rewound to `build` because the grouping key was wrong.
`a862dc7` fixed it. The rewind is the load-bearing lesson here.

## THE BUG THAT ESCAPED TESTS: per-key grouping keyed on subject alone, not (aggregate, subject)

`__outbox` is a single table shared across every tracked source table. The
per-key ordering group key in `58f9fba` was
`key = "" + ComputeSubject(model, row)` — the primary key only. Two
different source tables that happen to share a PK value (`widgets` pk=1 and
`orders` pk=1 both serialize to subject `"1"`) collapsed into the *same*
grouping bucket. A transiently-stuck `widgets/1` then head-of-line-blocked
`orders/1`, violating the acceptance criterion that a backing-off key must
block only later events for the *same* key — the code's own comment already
said "Group by aggregate + subject", but the implementation didn't do it.

All of `b6619e1`'s tests used a single aggregate (`main.widgets`), so they
passed against the broken key — a single-aggregate per-key-ordering test is
**vacuous** for this defect class; it cannot distinguish "grouped by subject"
from "grouped by aggregate+subject" because there is only one aggregate to
collide with. Review attempt 1 caught it by reading `OutboxDispatcher.cs:210`
against its own comment at line 200, not by running a test. `a862dc7` fixed
the key to `(row[ColAggregate], ComputeSubject(model, row))` and added
`PerKeyOrdering_StuckAggregateDoesNotBlockOtherAggregateSharingPk`, which
seeds `widgets/1` + `orders/1` (same subject `"1"`), fails only `widgets`,
and asserts `orders/1` still delivers — against the old key this assertion
fails (both collapse into one bucket, `orders` never delivers), so the test
is non-vacuous proof of the fix.

**Generalize**: any "per-key" fairness/ordering/rate-limit scheme built over
a table shared by multiple sources/tenants must key on the FULL identity
(source/tenant + key), never the key component alone. Any test proving
per-key isolation over such a table must use **at least two distinct
sources sharing a key value** — a single-source test cannot distinguish a
correct composite key from a broken component-only key and gives false
confidence.

## Collision-safe composite keys: value tuple, not string concat

The fix groups by the `(string aggregate, string subject)` value tuple
(`GroupBy` default comparer → `ValueTuple` structural equality, ordinal
`String.Equals` per component), not `aggregate + subject` string
concatenation. Concatenation reintroduces the exact bug it fixes:
`"a" + "bc" == "ab" + "c"` under naive concat, so two different
(aggregate, subject) pairs can still collide into one bucket. Any composite
grouping/dedup key built from two independently-attacker-or-source
-controlled string components should use a tuple (or an explicit separator
byte proven absent from both components, as the unparseable-subject sentinel
`"\0unparseable"` does here) — never bare concatenation.

## Dead-letter boundary discipline

The dead-letter test pins the exact boundary: an always-failing sink flips
`dead=true` at **exactly** `maxAttempts` attempts (not "eventually"), the
sink is invoked exactly `maxAttempts` times, and the row is never
re-dispatched or deleted afterward (`dispatched_at IS NULL AND dead=false`
stays the eligibility filter). Off-by-one on this boundary either dead-letters
one attempt early (dropping a deliverable event) or never dead-letters
(unbounded retry loop) — assert the exact count, not a directional trend.

## At-least-once + idempotency key, not exactly-once

`IEventSink.DeliverAsync` gained an explicit idempotency key (the CloudEvents
`id`, i.e. the outbox row's stable id). Delivery is at-least-once by
construction: a row can be delivered and then the dispatcher crashes before
stamping `dispatched_at`, redelivering the same event on the next pass. The
key lets a sink/downstream consumer de-dupe — but de-dup is a **stated
caveat on the consumer**, documented on the `IEventSink` interface itself
(the one durable place in scope), not a guarantee the dispatcher provides.
Do not claim exactly-once for any at-least-once delivery path; document the
crash window and hand the consumer the dedup key instead.

## max-attempts is a dispatcher option, not per-table metadata

The dead-letter threshold (`DefaultMaxAttempts=5`, floored to 1) is a
constructor/options value on `OutboxDispatcher`, alongside the existing
backoff constants — a delivery-policy knob, not a `MetadataKeys`-surfaced
per-table setting. Keeping it off the metadata surface avoided touching the
metadata validator / `KnownTableKeys` allow-list for a value that is a
dispatcher-wide operational tuning parameter, not a per-table schema
concern. Apply the same split to future delivery-policy knobs (retry caps,
backoff ceilings, batch sizes): dispatcher option by default; only promote
to per-table metadata if a real per-table override requirement shows up.
