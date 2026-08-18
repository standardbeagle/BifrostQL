---
title: "Data retention and right-to-erasure, driven by table metadata"
published: false
description: "Purge rows on a schedule with retain and ttl windows, keep every sweep tenant-scoped through the mutation pipeline, and tombstone the change-history trail when a tracked row is physically erased."
tags: gdpr, database, dotnet, compliance
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/retention/
---

Two metadata keys give a table a retention policy that runs by itself:

```text
main.sessions  { ttl: 30d after last_seen }
main.audit_log { soft-delete: deleted_at; retain: 90d }
```

A hosted service starts with the app, wakes every hour, and deletes what has aged out — expired live
sessions, and audit rows that have been soft-deleted longer than the window. Each delete goes through
the full mutation pipeline, so tenant scoping, soft-delete semantics, change history, and CDC events
all apply to a purge exactly as they apply to a user's delete. When a physical erasure removes a
history-tracked row, the trail is tombstoned rather than appended to, so the purge does not
re-persist the data it exists to remove.

The runs below are real: a BifrostQL host on SQLite, seeded rows, one purge pass, before and after.

## Two windows answering two questions

`retain` and `ttl` look similar and are deliberately not interchangeable.

**`retain`** is how long an **already-soft-deleted** row is kept before it is physically removed. It
takes a bare duration and always anchors on the table's soft-delete column. Because the anchor is the
soft-delete timestamp, `retain` can never select a live row — a live row's timestamp is null and
cannot match the cutoff. That is a property of the anchor, not a filter the sweeper has to remember.
It requires a `soft-delete` column, and it removes rows through the pipeline's declared hard-delete
route, gated by the table's `soft-delete-hard-role`.

**`ttl`** expires **live** rows, so it demands an explicit anchor: `<duration> after <column>`, where
the column exists and is timestamp-typed. It routes a plain delete and lets the pipeline decide the
semantics — on a table with a soft-delete column the expiry becomes a soft delete, and on a table
without one it is a physical delete.

Durations are a positive integer plus `d` or `h`. A common pairing is `ttl` then `retain`: expiry
soft-deletes a live row, and the retention window later hard-purges it.

## One pass, watched

I seeded three sessions and four audit rows, then started the host with the policies above. Before:

```
sessions                              audit_log
1|stale-2024 |2024-06-01              1|soft-deleted 2y ago  |2024-08-01
2|stale-31d  |2026-07-16              2|soft-deleted 91d ago |2026-05-18
3|fresh-2d   |2026-08-15              3|soft-deleted 8d ago  |2026-08-09
                                      4|live row             |NULL
```

After a single pass — no request, no feature flag, just the app starting:

```
sessions                              audit_log
3|fresh-2d   |2026-08-15              3|soft-deleted 8d ago  |2026-08-09
                                      4|live row             |NULL
```

Both stale sessions are gone. `sessions` has no soft-delete column, so a `ttl` expiry there is a
physical delete. In `audit_log`, the two rows soft-deleted longer than 90 days are physically gone,
the one soft-deleted 8 days ago stayed, and the live row was never a candidate — that is the
structural guarantee, visible.

The host log shows the bounded selection read that fed the sweep:

```
Query transformed: Table=audit_log FilterApplied=True
Query completed:   Table=audit_log RowCount=2
Query transformed: Table=sessions  FilterApplied=True
Query completed:   Table=sessions  RowCount=2
```

Two candidates per table, read through the query seam, then deleted one at a time.

## Tenant safety without a caller

A background purge carries no caller identity, which is the interesting problem. For a tenant-filtered
table the engine synthesizes a per-tenant system context and runs every delete under it, so the
pipeline's tenant transformer narrows each write. An out-of-scope primary key matches zero rows: a
sweep for tenant A cannot reach tenant B's rows because the boundary is enforced by the same
transformer that enforces it for a user request. The only cross-tenant read is a `SELECT DISTINCT` of
the tenant column — identifiers, never row data — so the sweep knows which scoped passes to run. The
expired-key reads run under the same per-tenant context, so they only ever see that tenant's rows.

## Bounded and resumable

Each pass reads at most 100 expired keys per table, tenant, and window, and **each delete commits in
its own transaction**. There is no long-running transaction spanning a batch. A crash mid-sweep
leaves every committed delete in place; the next pass re-reads the shrunken expired set and carries
on. Progress only moves forward.

The loop also self-disables. On its first pass, a model where no table declares `retain` or `ttl`
makes the engine log a debug line and return, so a host with no retention policy pays nothing for the
service being registered. A table whose retention config fails to parse counts as not opting in,
which means a broken policy never keeps the loop alive and never deletes anything.

Production settings are a one-hour poll and 100 rows per table, tenant, and mode per pass. The
service starts detached, so a slow first pass never delays app start, and a failed pass is logged and
retried on the next interval instead of being thrown at the host.

## Erasure tombstones the trail

Here is the decision worth reading closely. A retention purge deletes through the same mutation
pipeline as any other delete, so on a table that records change history the history writer fires —
inside the purge's own transaction. Its default behavior for a delete is to record the row's
before-image. For an erasure that would re-persist the exact data the purge exists to remove, and the
entity's *existing* trail rows are that data too.

So when a purge **physically** removes a tracked row, the history writer instead:

1. deletes the entity's existing trail rows, scoped by the entity's serialized primary key, so no
   before/after images survive the erasure; and
2. writes one payload-free tombstone — `op = 'erase'`, `before` and `after` both null,
   `changed_columns` empty — recording who erased what and when.

Both statements run on the purge's own connection and transaction, so the trail change commits or
rolls back atomically with the delete. A rolled-back purge leaves no orphan history row and no orphan
CDC outbox row. Both are direct SQL that never re-enters the mutation pipeline, so there is no
history-of-history and the tombstone carries no before-image to capture — the erasure terminates by
construction.

Two consequences to check when you audit a deployment. A soft delete is **not** an erasure: a `ttl`
expiry that the pipeline turns into a soft delete leaves the row present and records history
normally, and the data is not erased until it is physically removed. And the trail purge is scoped by
the entity's primary key, so a tenant-A erasure clears only entity A's trail.

This is covered by integration tests rather than by my curl transcript:
`ErasurePurge_TombstonesTheTrail_PurgesExistingImages_AndTerminates`,
`ErasurePurge_EmitsCdcEvent_InThePurgeTransaction`, and
`RolledBackErasure_LeavesNoOrphanTrailOrOutboxRow` in `RetentionErasureHistoryTests`.

## Failing toward not deleting

I tried a deliberately bad policy — `retain: 0d` — and the host said so precisely:

```
Invalid BifrostQL metadata configuration:
  main.audit_log [retain]: '0d' - 'retain' on 'main.audit_log' has an invalid duration '0d':
  the magnitude must be a positive integer. Use a positive integer followed by 'd' (days)
  or 'h' (hours), e.g. '90d' or '12h'.

fail: BifrostQL.Core.Modules.Retention.RetentionPurgeEngine[0]
      Retention purge pass failed; retrying after the interval.
```

A row soft-deleted two years earlier was still sitting there afterwards. Worth noting for anyone
matching this against the guide: a duration this malformed is caught by model validation, so the
whole pass fails rather than skipping one table. The per-table skip described in the docs covers
configuration that survives model load — a `retain` on a table with no soft-delete column, or a `ttl`
anchored on a non-timestamp column. Either way the failure direction is the same: nothing is purged.

A scoped-away delete — an out-of-tenant primary key — affects zero rows and is counted as zero
purged, so it neither deletes nor tombstones a trail it never touched.

## Dry-run first

The loop is on by default, and declaring `retain` or `ttl` is the switch. Within the hour, the next
pass acts on it. So run a dry-run before you enable a policy:

```csharp
var report = await engine.PurgeOnceAsync(model, connFactory, endpoint, ct, dryRun: true);

foreach (var c in report.Candidates)
    Console.WriteLine($"WOULD purge {c.Table} pk=[{string.Join(",", c.PrimaryKey)}] " +
                      $"({c.Mode}, tenant={c.Tenant})");
```

A dry-run enumerates candidates through the identical selection read a live pass uses and routes zero
delete intents, so the report is exactly what the next live pass will remove. On a live pass
`Candidates` is empty and `RowsPurged` reports what actually went. Configure the metadata, dry-run,
read every candidate — especially the tenant scope, and that no live row appears under a `retain`
window — then let the scheduled pass run. Re-run it after any policy change.
