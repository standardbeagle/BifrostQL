---
title: Emitting Change Events (CDC)
description: Configure a table to emit insert/update/delete events into a transactional outbox, across single-row, batch, and nested (TreeSync) writes.
---

BifrostQL can emit a domain event for every row change so downstream systems stay
in sync without polling. Events are written to a **transactional outbox** — the
same transaction as the data change — so they can never be lost or fabricated
relative to the write they describe. See
[Change Data Capture & Outbound Events](/concepts/cdc-outbound-events) for the
concept and the event envelope; this guide is the configuration walkthrough.

## 1. Create the outbox table

BifrostQL publishes an existing database, so you create the outbox table. It must
carry the documented column contract (validated at model load). SQLite example:

```sql
CREATE TABLE __outbox (
    id            INTEGER PRIMARY KEY,
    aggregate     TEXT NOT NULL,
    op            TEXT NOT NULL,
    payload       TEXT NOT NULL,
    tenant        TEXT NULL,
    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
    dispatched_at TEXT NULL,
    attempts      INTEGER NOT NULL DEFAULT 0,
    dead          INTEGER NOT NULL DEFAULT 0
);
```

See the concept page for SQL Server and Postgres DDL.

## 2. Point the model at the outbox

Add a model-level (`:root`) rule naming the outbox table:

```text
:root { outbox-table: dbo.__outbox }
```

This is required as soon as any table opts into events; model load fails fast if
the named table is missing or does not carry the full column contract.

## 3. Opt a table into events

Declare which operations emit, and (optionally) the payload mode:

```text
dbo.orders { emit-events: insert,update,delete; event-payload: changed }
```

- `emit-events` — any subset of `insert`, `update`, `delete`.
- `event-payload` — `keys` (PK only), `changed` (written columns + key, the
  default-worthy middle ground), or `full`. Omitted defaults to `full`.

## 4. What gets captured, on every write path

The writer runs after the row is written but inside the same transaction, so:

- **Inserts capture the database-generated key.** An event for a new row always
  identifies it, even when the client did not supply the id.
- **Zero-row updates/deletes emit nothing.** An out-of-scope tenant/policy no-op
  does not fabricate an event.
- **All write paths are covered**, identically:
  - single-row `orders(insert: …)`, `orders(update: …)`, `orders(delete: …)`
  - batch `orders_batch(actions: […])` — one event per action
  - nested `orders(sync: { … })` (TreeSync) — one event per inserted/updated/
    deleted row in the tree, with child foreign keys resolved to the parent's
    generated key

If an event cannot be written (e.g. its key could not be captured), the whole
mutation rolls back rather than committing a change with no event.

## 5. Tenancy

The event's `tenant` column is filled from the user context using the model's
`tenant-context-key` (default `tenant_id`), so multi-tenant consumers can filter
the stream per tenant.

## Delivery

Draining the outbox to webhooks/queues — retries, dead-lettering, and the
`webhook-secret` signature — is handled by the dispatcher, a separate component.
Until it runs, events accumulate durably in the outbox table (that is the point
of the outbox: the write path never blocks on delivery).
