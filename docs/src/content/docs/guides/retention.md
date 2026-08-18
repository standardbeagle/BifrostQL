---
title: "Data Retention and Right to Erasure"
description: "Purge data on a schedule with retain and ttl policies, tombstone the change-history trail on erasure, and dry-run a policy before the purge service enacts it."
---

BifrostQL can purge data on a schedule, driven entirely by table metadata. A retention
policy answers two very different compliance questions with two deliberately separate keys:

- **`retain`** — how long an **already-soft-deleted** row is kept before it is
  **hard-purged** (physically removed). Anchored on the table's soft-delete column.
- **`ttl`** — how long a **live** row is kept before it **expires**. Anchored on an
  explicit timestamp column you name.

The purge is compliance-sensitive: it is how a deployment satisfies **right-to-erasure**
(GDPR Art. 17 and equivalents). This guide covers the two windows, how a purge stays
tenant-safe, what happens to the change-history trail of an erased row, and — most
important — the **dry-run you should run before enabling any policy**.

## Configure a policy

Retention is opt-in per table. A table with neither key is **never** purged — there is no
implicit default window, ever.

```
-- Hard-purge rows that have been soft-deleted for more than 90 days.
"dbo.audit_log { soft-delete: deleted_at; soft-delete-hard-role: purge_admin; retain: 90d }"

-- Expire live sessions 30 days after they were last seen.
"dbo.sessions { ttl: 30d after last_seen }"
```

Durations are a positive integer followed by `d` (days) or `h` (hours): `90d`, `12h`. A
zero, negative, or malformed duration is a configuration error and the table is skipped
fail-closed (see [Fail-closed behavior](#fail-closed-behavior)).

### `retain` — hard-purge already-soft-deleted rows

`retain` takes a **bare duration** and anchors on the table's
[soft-delete](/BifrostQL/guides/change-history/) column. Because the anchor is the
soft-delete timestamp, **`retain` can never select a live row** — a live row has a null
soft-delete timestamp and cannot match the cutoff. This is structural, not a filter the
scheduler must remember to apply.

`retain` **requires** a `soft-delete` column (a table with no soft-delete has no
already-soft-deleted rows to purge). To physically remove a row that a soft-delete UPDATE
could never touch, the purge uses the pipeline's **declared hard-delete route**, gated by
the table's `soft-delete-hard-role`. Configure that role so the system purge is authorized;
a table with no declared role needs none.

`retain` takes **no `after <column>` clause** — redirecting it onto a live-row column is
exactly what `ttl` is for, and mixing the two is rejected at load time.

### `ttl` — expire live rows

`ttl` expires **live** rows and is destructive, so it requires an **explicit** anchor:
`<duration> after <column>`. The column must exist and be a timestamp type. `ttl` routes a
plain delete and lets the pipeline decide the semantics:

- On a table **with** a soft-delete column, a `ttl` expiry becomes a **soft delete** (the
  row is flagged, not removed). The engine never special-cases this — the pipeline decides.
- On a table **without** a soft-delete column, a `ttl` expiry is a **hard delete**.

A common lifecycle is `ttl` **then** `retain`: `ttl` soft-deletes an expired live row, and
`retain` later hard-purges it once it has been soft-deleted long enough.

## Tenant-safe by construction

A background purge carries **no caller identity**. For a
[tenant-filtered](/BifrostQL/guides/org-model/) table the engine synthesizes a **per-tenant
system context** and runs every delete under it, so the mutation pipeline's tenant
transformer narrows each write to that tenant. An out-of-scope primary key therefore matches
**zero rows** — a sweep for tenant A can never reach tenant B's rows, because the pipeline
enforces the boundary, not because the engine remembered to filter. The only cross-tenant
read is a `SELECT DISTINCT` of the tenant column (tenant identifiers only, never row data)
so the sweep can fan out one scoped purge per tenant.

Reads are scoped the same way: the bounded expired-key SELECT runs through the read seam
under the same per-tenant context, so it only ever sees that tenant's expired rows.

## Bounded, resumable passes

Each pass reads at most a bounded batch of expired keys per (table, tenant, window), and
**each delete commits in its own transaction**. There is no long-running transaction and no
partial batch. A crash mid-sweep leaves every already-committed delete in place; the next
pass re-reads the shrunken expired set and continues. Progress is monotonic and never
re-scans from zero.

## Right-to-erasure and the change-history trail

This is the load-bearing compliance decision, so it is worth stating precisely.

A retention purge deletes a row through the **same mutation pipeline** as any other delete,
so if the table also [records change history](/BifrostQL/guides/change-history/), the
history writer fires for the purge — **inside the purge's own transaction**. Its default
behavior for a delete is to record the row's *before-image* into the trail. For an erasure
that would be a serious defect: it would **re-persist the very PII the purge exists to
erase**, and the entity's *existing* trail rows (the before/after images from prior inserts
and updates) are themselves that PII.

BifrostQL's policy is **tombstone the trail, not append to it**. When a retention purge
**physically removes** a tracked row, the history writer instead:

1. **Purges the entity's existing trail rows** (a direct, dialect-rendered `DELETE` on the
   purge's own transaction), so no before/after PII survives the erasure; and
2. **Records one payload-free tombstone** — `op = 'erase'`, `before` and `after` both
   `NULL`, `changed_columns` empty — so the **fact** of erasure (who, when, which entity)
   remains auditable without retaining the data.

Both writes run on the purge's own connection and transaction, so the trail change **commits
or rolls back atomically with the delete**: a rolled-back purge leaves **no orphan** history
or CDC-event row. Likewise, the [CDC outbox](/BifrostQL/guides/cdc-events/) event for the
delete is written by the existing in-transaction hook in the same transaction — atomic with
the deletion, not a best-effort write before it.

The erasure **terminates** by construction. The trail-purge and the tombstone are direct SQL
that never re-enter the mutation pipeline, so neither triggers "history of history", and the
tombstone carries no before-image to re-capture. There is no way for the erasure to
regenerate the data it just erased.

Two properties follow, and are worth checking when you audit a deployment:

- **A soft delete is not an erasure.** A `ttl` expiry that the pipeline turns into a *soft*
  delete leaves the row present and records history **normally** — only a **physical**
  removal (a `retain` hard-purge, or `ttl` on a table with no soft-delete) tombstones the
  trail. The data has not actually been erased until it is physically removed.
- **Tenant isolation extends to the trail.** The trail purge is scoped by the entity's
  serialized primary key, so a tenant-A erasure clears only entity A's trail; another
  tenant's trail rows are untouched.

## Fail-closed behavior

Every failure mode fails safe — toward **not purging** — never toward an unscoped delete:

- An **invalid config** on a table (bad duration, `retain` without soft-delete, `ttl`
  without its anchor, a non-timestamp anchor) skips **only that table** for the pass and is
  logged; it never aborts the whole sweep and never widens a purge.
- A **scoped-away** delete (an out-of-tenant primary key) affects zero rows and is counted
  as zero purged — it neither deletes nor tombstones a trail it never touched.
- If a table declares no retention at all, it is never touched.

## Run a dry-run BEFORE enabling a policy

**Always dry-run a new or changed retention policy before letting it delete anything.** A
dry-run **enumerates and reports the exact rows the purge would remove — via the same
selection read a live pass uses — and routes zero delete intents, so nothing changes.**
Because the candidate set comes from the identical read a live pass then deletes, the report
is precisely what the next live pass will remove.

```csharp
// dryRun: true enumerates candidates and changes nothing.
var report = await engine.PurgeOnceAsync(model, connFactory, endpoint, ct, dryRun: true);

foreach (var candidate in report.Candidates)
{
    // candidate.Table, candidate.Mode (Retain | Ttl), candidate.Tenant, candidate.PrimaryKey
    Console.WriteLine($"WOULD purge {candidate.Table} pk=[{string.Join(",", candidate.PrimaryKey)}] " +
                      $"({candidate.Mode}, tenant={candidate.Tenant})");
}
```

Inspect `report.Candidates`, confirm the set matches what you intend to erase, and only then
enable the live pass. On a **live** pass `Candidates` is empty and `RowsPurged` reports the
rows actually removed. Recommended workflow:

1. Configure the `retain` / `ttl` metadata on the table.
2. Run a **dry-run** and review every candidate — especially the tenant scope and that no
   live rows appear for a `retain` window.
3. Enable the scheduled live purge.
4. Re-run the dry-run after any policy change.

## What each field means at a glance

| Key | Purges | Anchor | Delete semantics |
|-----|--------|--------|------------------|
| `retain: 90d` | rows already soft-deleted > 90d | soft-delete column (fixed) | hard delete via declared hard-delete route |
| `ttl: 30d after created_at` | live rows older than 30d | the named timestamp column | pipeline decides: soft delete if the table has one, else hard delete |

Both windows are tenant-scoped, bounded, resumable, and — on a physical removal of a
history-tracked row — tombstone the change-history trail atomically with the delete.
