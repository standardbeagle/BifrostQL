---
title: Recording Change History
description: Configure a table to record who changed which field, from what to what — a before/after trail written in the same transaction as the change, across single-row, batch, and nested (TreeSync) writes.
---

BifrostQL can record a **before/after trail** of every change to a table: which fields
moved, from what value to what, by whom, and when. The history row is written in the same
transaction as the change, so the trail can never disagree with the data. See
[Temporal Change History](/concepts/temporal-history) for the concept and the row shape;
this guide is the configuration walkthrough.

## 1. Create the history table

BifrostQL publishes an existing database, so you create the history table. It must carry
the documented column contract (validated at model load). SQLite example:

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

## 4. Attribute the actor

The trail's `actor` is the user-context claim named by the model-level `user-audit-key` —
the same claim that stamps `updated_by`, so a row and its trail can never disagree about
their author:

```text
:root { history-table: dbo.__history; user-audit-key: sub }
```

Without it, `actor` is null. It is never taken from client input.

## 5. Read the trail

There is no generated history field yet — `orders_history(...)` and as-of reads are a
planned slice. Until then the trail is read as an ordinary table: from the database
(reporting, SQL), or through the GraphQL API if you publish it like any other table.

:::note
GraphQL reserves names beginning with `__`, so a table literally named `__history` cannot
be exposed as a GraphQL field. Name it without the double underscore — `dbo.order_history`
— if you want to query the trail through the API. The DDL above uses `__history` for a
trail you read from the database.
:::

:::caution
The history table is a **table like any other**, so it is exposed and authorized like any
other. It holds every recorded value of every tracked column, which makes it as sensitive
as the most sensitive column it tracks. Protect it deliberately — a
`policy-read-deny-roles` rule restricting it to a compliance role, or a per-table
history table you grant separately. Do not leave it readable by everyone who can read the
tracked table's *current* row: the trail also contains the rows they were never allowed to
see, and every value that was since corrected.
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
