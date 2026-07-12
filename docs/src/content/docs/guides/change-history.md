---
title: Recording Change History
description: Configure a table to record who changed which field, from what to what — a before/after trail written in the same transaction as the change, across single-row, batch, and nested (TreeSync) writes.
---

BifrostQL can record a **before/after trail** of every change to a table: which fields
moved, from what value to what, by whom, and when. The history row is written in the same
transaction as the change, so the trail can never disagree with the data. See
[Temporal Change History](/concepts/temporal-history) for the concept and the row shape;
this guide is the configuration walkthrough.

:::caution[Breaking change: history tables are no longer published]
A table that is a history target (the model-level `history-table` default or any
per-table override) is a **system table**: it has **no root query field, no mutation
field, and no join/link navigation** in the generated schema, and the protocol-adapter
intent executors reject it — reads *and* writes. If you previously queried a published
history table directly, switch to the generated
[`<table>History` trail read field](#6-reading-the-trail), which is the only API path
to trail data and the reason for the change: direct access carried none of the trail's
authorization (entity discriminator, tenant scope, encrypted-image masking).
:::

## 1. Create the history table

BifrostQL publishes an existing database, so you create the history table. It must carry
the documented column contract (validated at model load). Once named as a history
target it becomes a **system table** — never published as an ordinary table, readable
only through the generated trail fields. SQLite example:

```sql
CREATE TABLE __history (
    id              INTEGER PRIMARY KEY,
    entity          TEXT NOT NULL,
    entity_id       TEXT NOT NULL,
    op              TEXT NOT NULL,
    actor           TEXT NULL,
    changed_at      TEXT NOT NULL,
    before          TEXT NULL,
    after           TEXT NULL,
    changed_columns TEXT NULL
);
CREATE INDEX ix___history_entity ON __history (entity, entity_id, id DESC);
```

See the concept page for SQL Server and Postgres DDL.

## 2. Point the model at it

Add a model-level (`:root`) rule naming the shared history table:

```text
:root { history-table: dbo.__history }
```

A table can override that with its own (`dbo.orders { history-table: dbo.orders_history }`)
when its retention, volume, or access control differs — same contract, different table.

## 3. Opt a table in

```text
dbo.orders {
  history: enabled;
  history-columns: status,total
}
```

- `history: enabled` records inserts, updates, and deletes. Narrow it to the operations
  you care about — `history: update` is the common "track field edits" case.
- `history-columns` narrows *what* is tracked. Omit it to track every column. Narrowing
  keeps noise out of the trail: an update that moves no tracked column records nothing.

Model load now fails fast if the history table is missing a contract column, if
`history-columns` names a column that does not exist, or if the history table is itself
tracked. A misconfiguration surfaces at startup, not on the first write.

## 4. Tenant-scoped tables: add the scope column

If a tracked table is tenant-isolated (`tenant-filter`), its history table must also carry
a column **with the same name** as the tracked table's tenant column — model load fails
fast if it is missing:

```sql
ALTER TABLE __history ADD tenant_id INT NULL;
```

Every trail row then materializes the tracked row's tenant value into that column — the
after-image value on an insert or update, the before-image value on a delete — in the same
transaction as the change. That is what lets history reads be authorized with plain column
predicates (the same `tenant-filter` mechanics as any other table) instead of parsing the
JSON images. The copy happens even when `history-columns` excludes the tenant column from
the recorded images.

A **shared** history table serving tracked tables with *different* tenant column names
must carry every one of those columns (nullable — each row fills only its own table's
column). If that gets unwieldy, give each table its own `history-table` override instead.

:::caution[Backfill before you rely on scoped reads]
Trail rows written **before** you add the column predate it and hold `NULL` scope. On
the trail read field (see [Reading the trail](#6-reading-the-trail)), `NULL`-scope rows
are **invisible to scoped readers by design** — the trail fails closed rather than
leaking rows whose tenant is unknown.
Backfill the column from the tracked table (or the recorded images) if the older trail
must remain visible to tenant-scoped readers:

```sql
UPDATE __history
SET tenant_id = json_extract(COALESCE(after, before), '$.tenant_id')
WHERE entity = 'dbo.orders' AND tenant_id IS NULL;
```

Only backfill from the images if the tenant column was tracked at the time; otherwise
join back to the tracked table by `entity_id`.
:::

## 5. Attribute the actor

The trail's `actor` is the user-context claim named by the model-level `user-audit-key` —
the same claim that stamps `updated_by`, so a row and its trail can never disagree about
their author:

```text
:root { history-table: dbo.__history; user-audit-key: sub }
```

Without it, `actor` is null. It is never taken from client input.

## 6. Reading the trail

Every history-enabled table gets a generated **trail read field** on the root query type:
`<table>History` — `ordersHistory` for `dbo.orders`. It returns the table's own trail
rows from its resolved history table, paged like any other generated field:

```graphql
query {
  ordersHistory(
    filter: { op: { _eq: "update" }, changed_at: { _gte: "2026-01-01" } }
    sort: [id_desc]
    limit: 50
  ) {
    total
    data { id entity_id op actor changed_at before after changed_columns }
  }
}
```

**Row shape.** The rows are the documented history contract — `id`, `entity`,
`entity_id` (the JSON key object), `op`, `actor`, `changed_at`, `before`, `after`
(the JSON images), `changed_columns` — plus the tenant scope column when the tracked
table is tenant-filtered. The field reuses the history table's generated filter type,
sort enum, and paged type, so the operator vocabulary is the one you already know:
`entity_id: { _eq: ... }` for one row's trail, `changed_at` ranges
(`_gt`/`_lt`/`_gte`/`_lte`/`_between`), `op` and `actor` equality, and `sort`/`limit`/
`offset`/`total` paging. The field exposes plain trail columns only — no relationship
or join fields.

**The entity discriminator is server-side.** On a shared history table,
`ordersHistory` always applies `entity = 'dbo.orders'` inside the server, ANDed with
your filter — a filter can narrow within the table's own trail but can never widen the
field to another table's rows.

**Tenant authorization is fail-closed.** For a tenant-filtered tracked table the field
adds the caller's tenant claim (the same `tenant-context-key` claim the base table's
tenant filter reads) as a plain predicate on the materialized scope column — that is
what the column exists for. A caller with no tenant claim gets **zero rows**, and
`NULL`-scope legacy rows are invisible to scoped callers (see the backfill note above).

**Encrypted images obey the read policy.** A recorded value of an
[encrypted column](/concepts/field-encryption) is stored in the images as ciphertext.
The trail read field passes it through the same decrypt/mask projection as a base-table
read: a caller holding the column's `unmask-role` (or admin) sees plaintext inside
`before`/`after`; every other caller sees the column's configured mask; raw ciphertext
is never returned, so the trail cannot serve as a decryption oracle.

**Policy row scoping is a current limitation.** A `policy-row-scope` expression has no
materialized column on the history table to re-apply it to, so the trail read field
cannot enforce it. Rather than expose trail rows of rows the caller is scoped out of,
**model load fails fast** when a table combines `policy-row-scope` with `history`.
Remove the row scope or disable history on that table; an explicit grant mechanism may
lift this later.

**Name collisions fail fast.** If a real table's GraphQL name equals a generated
`<table>History` field name, model load fails with an error naming both tables — rename
one or disable history.

**The history table itself is unpublished.** A history target is a **system table**: it
has no root query field, no mutation field, and no join/link navigation, and the
protocol-adapter intent executors (`IQueryIntentExecutor` / `IMutationIntentExecutor`)
reject it with an access-denied error — reads *and* writes. Trail rows are written only
by the change-history writer, inside the tracked write's transaction; the generated
`<table>History` fields are the only API read path. The field is named after the
*tracked* table, so any history table name works — including `__history`, whose `__`
prefix could never be a GraphQL field of its own.

:::caution
The trail's history table can still be read **at the database level** (reporting, SQL,
backups), and it holds every recorded value of every tracked column — as sensitive as
the most sensitive column it tracks. Grant direct SQL access to it as deliberately as
you would a credentials table. Within the API, the `<table>History` field applies the
tracked table's tenant scope, but a caller who can read a tracked table's *current* rows
can also read that table's trail — including values that were since corrected. Narrow
`history-columns` if some columns should never enter the trail at all.
:::

## What gets recorded

| Write | Recorded |
|-------|----------|
| Insert | `after` only; the key comes from the database-generated identity when the client did not supply it. |
| Update | `before` + `after`; `changed_columns` lists the tracked columns that actually moved. Nothing is recorded if none did. |
| Delete | `before` only. |
| Soft delete | An **update** — that is what it is at the row level (`deleted_at` moved). |
| Upsert that inserts | An **insert** — nothing existed before it. |
| Batch / nested TreeSync | Each action and each tree operation records its own row, on that batch's or sync's single transaction. |
| A write that affects no row | Nothing — an out-of-scope tenant/policy no-op never fabricates a change. |

Every recorded value is the value **as stored**: the writer reads the row back rather than
echoing the mutation's inputs, so DB defaults, triggers, and computed columns appear in the
trail as the database actually holds them. An [encrypted column](/concepts/field-encryption)
is therefore recorded as its ciphertext — the trail never becomes a plaintext side-channel
around field encryption. Exclude such a column with `history-columns` if you do not want
even its ciphertext duplicated.

## Concurrency

The before-image is read immediately before the write, in the write's own transaction —
and that read **locks the row for the transaction**. Without the lock, a concurrent
transaction could commit its own change between the pre-image read and the update, and
the trail would attribute that writer's changes to this actor with a `before` that never
matched the actual pre-write state. With it, a concurrent writer simply blocks until this
transaction ends, exactly as if the two writes had arrived in sequence.

The lock takes each database's native form:

| Database | Form |
|----------|------|
| SQL Server | `WITH (UPDLOCK)` table hint on the pre-image `SELECT` |
| PostgreSQL | trailing `FOR UPDATE` |
| MySQL | trailing `FOR UPDATE` |
| SQLite | nothing — write locking is whole-database, so the transaction already serializes concurrent writers |

Only the before-image capture read locks; ordinary reads (the after-image read-back
included — the write itself already holds the row's lock by then) are unchanged. The
emitted SQL shape is pinned per dialect in the test suite; the lock's hold-until-commit
semantics are each database's documented guarantee, so no cross-process race is
simulated in CI.

## Requirements and limits

- **Updates and deletes must be primary-key scoped.** A predicate-only write (delete every
  `status: "archived"` row) can match an unbounded set the writer cannot enumerate, so it
  is rejected on a history-enabled table rather than committed with no trail.
- **Recording is part of the write.** If the history row cannot be written, the change is
  rolled back. That is the point — a change that is not recorded did not happen — but it
  does mean a broken history table takes writes down with it, which is why the contract is
  validated at model load.
- **Growth is real.** Every tracked change is a row, forever, until you prune it.

## Retention

BifrostQL does not prune the trail: retention is a database concern, and the right policy
is domain-specific (a dispute window, a regulatory minimum). Run it as a scheduled job
against the history table:

```sql
-- Keep two years of trail.
DELETE FROM __history WHERE changed_at < DATEADD(year, -2, SYSUTCDATETIME());
```

Two interactions worth planning for:

- **Purging the tracked table does not purge its trail.** Deleting a row records a
  `delete` and leaves every earlier row of its history in place — deliberately: that is
  the record of what was deleted. If a data-subject erasure request must remove the
  values too, it has to reach the history table as well. Plan that before you need it.
- **A per-table history table** is the simplest way to give one table a different
  retention clock (or a different grant) from the rest of the model — that is what the
  table-level `history-table` override is for.
