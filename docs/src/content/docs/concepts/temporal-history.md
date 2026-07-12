---
title: Temporal Change History
description: Record who changed which field, from what to what, and when — a metadata-driven before/after trail written atomically with the change, beyond the created/updated audit columns.
---

The audit-column module stamps *who touched a row last*. **Temporal change history**
records the rest: **which fields changed, from what value to what value, by whom, and
when** — one history row per recorded mutation, written in the *same database
transaction* as the change itself, so the trail can never disagree with the data.

This is the trail LOB and admin apps need for dispute resolution, field-level rollback,
and the "who set this to `cancelled` on Tuesday?" question.

:::note
The **metadata contract and the writer** are implemented. Inserts are recorded on every
write path; **updates and deletes are recorded on single-row mutations only** — the batch
and nested TreeSync paths do not yet run the pre-write phase a before-image needs, so an
update or delete through them against a history-enabled table is **rejected** rather than
committed with no trail (a later slice lifts this). The history **read surface**
(`orders_history(...)`, as-of reads) is also a later slice; until then the trail is
queried as an ordinary table.
:::

## Metadata

A table opts in by naming the operations it records. The history rows go to a table you
name — either a shared one for the whole model, or a per-table one.

```text
dbo.orders {
  history: insert,update,delete;
  history-table: dbo.orders_history;
  history-columns: status,total
}

:root {
  history-table: dbo.__history
}
```

### Table-level keys

- **`history`** — comma-separated list of operations recorded: `insert`, `update`,
  `delete` (case-insensitive), or the single token `enabled` meaning all three.
  Presence of this key is what opts the table in. A subset is allowed — `history: update`
  is the common "track field edits only" case.
- **`history-table`** — qualified name of *this* table's history table. Overrides the
  model-level default.
- **`history-columns`** — comma-separated allow-list of columns whose changes are
  recorded. Omit it to track every column. Narrowing keeps noisy columns (a
  `last_seen_at` heartbeat) and columns you would rather not duplicate out of the trail.
  Every named column must exist.

### Model-level keys

- **`history-table`** — the shared default history table (e.g. `dbo.__history`) used by
  every history-enabled table that does not override it.

## Per-table or shared?

Both are supported, and **both use the same column contract** — per-table versus shared
is a routing and partitioning decision, not a shape decision:

| | Shared (`:root { history-table: dbo.__history }`) | Per-table (`dbo.orders { history-table: dbo.orders_history }`) |
|---|---|---|
| Setup | One table for the whole model. | One table per tracked table. |
| Retention / purge | One policy, one job. | Per-table policies (purge `orders` history on a different clock than `patients`). |
| Growth | One hot table; the `entity` column discriminates. | Growth is isolated per table; indexes stay narrow. |
| Access control | One grant surface. | Grant history access per table (a compliance role can read `patients_history` alone). |

Start shared. Split a table out when its retention, volume, or access control genuinely
diverges — the metadata change is one key and the writer needs no code change.

## History table schema

Because BifrostQL publishes an existing database, you create the history table yourself.
Shared or per-table, it must expose this column contract (validated at model load):

| Column | Purpose |
|--------|---------|
| `id` | Surrogate primary key, monotonic — defines trail order. |
| `entity` | Qualified source table, e.g. `dbo.orders`. Constant in a per-table history table; the discriminator in a shared one. |
| `entity_id` | JSON object of the changed row's primary-key columns — an object, not a scalar, so composite keys record losslessly. |
| `op` | `insert` / `update` / `delete`. |
| `actor` | User id from the audit user-context (`user-audit-key`). Nullable: system and unauthenticated writes have no actor. |
| `changed_at` | Write timestamp. |
| `before` | JSON pre-image of the tracked columns. Null on `insert`. |
| `after` | JSON post-image of the tracked columns. Null on `delete`. |
| `changed_columns` | JSON array of the tracked columns that actually differed. |

### DDL

SQL Server:

```sql
CREATE TABLE dbo.__history (
    id               BIGINT IDENTITY(1,1) PRIMARY KEY,
    entity           NVARCHAR(256)  NOT NULL,
    entity_id        NVARCHAR(512)  NOT NULL,
    op               NVARCHAR(16)   NOT NULL,
    actor            NVARCHAR(128)  NULL,
    changed_at       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    before           NVARCHAR(MAX)  NULL,
    after            NVARCHAR(MAX)  NULL,
    changed_columns  NVARCHAR(MAX)  NULL
);
-- Trail lookup: one row's history, newest first.
CREATE INDEX IX___history_entity ON dbo.__history (entity, entity_id, id DESC);
```

Postgres:

```sql
CREATE TABLE public.__history (
    id               BIGSERIAL PRIMARY KEY,
    entity           TEXT        NOT NULL,
    entity_id        JSONB       NOT NULL,
    op               TEXT        NOT NULL,
    actor            TEXT        NULL,
    changed_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    before           JSONB       NULL,
    after            JSONB       NULL,
    changed_columns  JSONB       NULL
);
CREATE INDEX ix___history_entity ON public.__history (entity, (entity_id::text), id DESC);
```

A per-table history table uses the identical contract — only the name differs.

## History row

One recorded `update` on `dbo.orders` with `history-columns: status,total`:

```json
{
  "id": 918,
  "entity": "dbo.orders",
  "entity_id": { "id": 1007 },
  "op": "update",
  "actor": "u_44",
  "changed_at": "2026-07-11T22:04:11Z",
  "before": { "status": "packing", "total": 240.00 },
  "after":  { "status": "shipped", "total": 240.00 },
  "changed_columns": ["status"]
}
```

`total` appears in both images (it is tracked) but not in `changed_columns` (it did not
change) — so a reader gets the full tracked state *and* knows what moved.

:::caution
An [encrypted column](/concepts/field-encryption/) is recorded in `before`/`after` as the
ciphertext at rest, not the plaintext — the trail never becomes a plaintext side-channel
around field encryption. Exclude the column with `history-columns` if you do not want its
ciphertext duplicated into the trail at all.
:::

## How a change is recorded

The writer spans both halves of the mutation's transaction, because a before/after trail
needs one fact from each:

1. **Before the write** it reads the current row — the only moment the pre-image still
   exists — and holds it for this mutation.
2. **After the write, still inside the transaction**, it knows the result: whether a row
   actually changed, and the database-generated key of an `insert`. It reads the stored
   row back as the after-image, diffs the tracked columns, and inserts the history row.

Because the history row is written in the same transaction, a rejected write takes its
trail entry down with it, and a committed change always has one.

Consequences worth knowing:

- **The after-image is read back, not assembled** from the mutation's inputs, so DB
  defaults, triggers, and computed columns are recorded as they were *stored*.
- **An update that moved no tracked column records nothing.** That is the point of
  narrowing with `history-columns`: a heartbeat write does not become a trail entry.
- **A write that affected no row records nothing** — an out-of-scope tenant/policy no-op
  never fabricates a change.
- **The mutation must name its row.** A history-enabled table rejects an update or delete
  scoped only by a predicate (e.g. delete every `status: "archived"` row); such a write
  can match an unbounded set the writer cannot enumerate. Scope by primary key.
- **Batch and nested TreeSync updates/deletes on a history-enabled table are rejected**
  for now (see the note above) rather than committed without a trail. Inserts through
  those paths are recorded normally — an insert has no before-image to miss.
- A **workflow-triggered** write is recorded like any other: it is a real change, and the
  trail names its actor.
- A **soft delete** is recorded as an `update` (that is what it is at the row level: the
  `deleted_at` column moved), so `history: delete` alone does not record it.

## Validation

Misconfiguration fails fast at model load rather than leaving a hole in a trail nobody
inspects until it matters:

- An unrecognized `history` operation token is rejected (a typo would otherwise silently
  stop recording that operation).
- `history-table` / `history-columns` set without `history` is rejected — the table
  records nothing, and the author believes it does.
- A `history-columns` entry naming a column that does not exist is rejected.
- Every history-enabled table must resolve to a history table (its override, else the
  model default); the target must exist and carry every contract column.
- A history table may not be the tracked table itself, and may not itself set `history` —
  either would make the writer record its own writes.
