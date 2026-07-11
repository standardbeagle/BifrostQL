---
title: Change Data Capture & Outbound Events
description: Emit insert/update/delete domain events through a transactional outbox so webhooks, queues, and search indexers stay in sync without polling — metadata-driven, exactly-once, no app dual-writes.
---

BifrostQL can emit a **domain event** every time a row is inserted, updated, or
deleted, so downstream systems — webhooks, message queues, search indexers —
stay in sync without polling the database.

Events are written through a **transactional outbox**: the event row is written
in the *same database transaction* as the data change. Either both commit or
neither does, so an event can never be lost or fabricated relative to the write
it describes (the correct pattern versus lossy triggers or app-level dual-writes).

:::note
This page documents the **metadata contract and the outbox table** (CDC slice 1).
The before-commit writer that populates the outbox and the background dispatcher
that drains it to webhooks/queues are later slices; the keys below are recognized
and validated at model load today.
:::

## Metadata

A table opts in by declaring which mutations emit events. The model names the
outbox table those events are written to.

```text
dbo.orders {
  emit-events: insert,update,delete;
  event-sink: outbox;
  event-payload: changed
}

:root {
  outbox-table: dbo.__outbox;
  webhook-secret: <shared-signing-secret>
}
```

### Table-level keys

- **`emit-events`** — comma-separated list of operations that emit an event:
  `insert`, `update`, `delete` (case-insensitive). Presence of this key is what
  opts the table in. A subset is allowed (e.g. `emit-events: insert,update`).
- **`event-sink`** — the durable sink. The only value today is `outbox`. Omitting
  the key defaults to `outbox`. (Webhook and queue delivery are drained *from* the
  outbox by the dispatcher, not named here.)
- **`event-payload`** — how much of the row is captured. Defaults to `full`.

### Model-level keys

- **`outbox-table`** — qualified name of the transactional outbox table (e.g.
  `dbo.__outbox`). **Required** once any table sets `emit-events`. The table must
  already exist in the database and carry the [outbox column contract](#outbox-table-schema).
- **`webhook-secret`** — shared secret the dispatcher uses to sign webhook
  deliveries. Consumed by a later slice; allow-listed so configs may set it now.

### Payload modes

| Mode | Captured in `payload` |
|------|-----------------------|
| `full` | The entire post-image of the row. |
| `changed` | Only the columns that changed, plus the primary key. |
| `keys` | Primary key columns only (consumer re-reads the row). |

`keys` is the smallest and safest for sensitive tables; `full` is the most
convenient for consumers that cannot re-query. `changed` is a middle ground for
audit-style feeds.

## Outbox table schema

Because BifrostQL publishes an existing database, you create the outbox table
yourself. It must expose this column contract (validated at model load):

| Column | Purpose |
|--------|---------|
| `id` | Surrogate primary key, monotonic — defines drain order. |
| `aggregate` | Qualified source table, e.g. `dbo.orders`. |
| `op` | `insert` / `update` / `delete`. |
| `payload` | JSON event body per the payload mode. |
| `tenant` | Tenant id captured from the user context (nullable). |
| `created_at` | Write timestamp. |
| `dispatched_at` | Set by the dispatcher on successful delivery (nullable = undelivered). |
| `attempts` | Delivery attempt counter. |
| `dead` | Dead-letter flag once attempts are exhausted. |

### DDL

SQL Server:

```sql
CREATE TABLE dbo.__outbox (
    id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    aggregate      NVARCHAR(256)   NOT NULL,
    op             NVARCHAR(16)     NOT NULL,
    payload        NVARCHAR(MAX)    NOT NULL,
    tenant         NVARCHAR(128)    NULL,
    created_at     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    dispatched_at  DATETIME2        NULL,
    attempts       INT              NOT NULL DEFAULT 0,
    dead           BIT              NOT NULL DEFAULT 0
);
-- Drain index: undelivered, non-dead rows in write order.
CREATE INDEX IX___outbox_undispatched ON dbo.__outbox (id) WHERE dispatched_at IS NULL AND dead = 0;
```

Postgres:

```sql
CREATE TABLE public.__outbox (
    id             BIGSERIAL PRIMARY KEY,
    aggregate      TEXT        NOT NULL,
    op             TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    tenant         TEXT        NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    dispatched_at  TIMESTAMPTZ NULL,
    attempts       INT         NOT NULL DEFAULT 0,
    dead           BOOLEAN     NOT NULL DEFAULT false
);
CREATE INDEX ix___outbox_undispatched ON public.__outbox (id) WHERE dispatched_at IS NULL AND dead = false;
```

## Event envelope

Each outbox row is one event. Consumers see this shape (the `payload` column
holds the row body; the surrounding columns are the envelope):

```json
{
  "id": 4821,
  "aggregate": "dbo.orders",
  "op": "update",
  "tenant": "acme",
  "created_at": "2026-07-11T22:04:11Z",
  "payload": {
    "id": 1007,
    "status": "shipped"
  }
}
```

The example uses `event-payload: changed`, so `payload` carries only the changed
column (`status`) plus the key (`id`).

## Validation

Misconfiguration fails fast at model load rather than aborting a real write later:

- An unrecognized `emit-events` operation, `event-sink`, or `event-payload` value
  is rejected (a typo would otherwise silently never emit).
- If any table sets `emit-events` but no `outbox-table` is configured, or the
  named outbox table does not exist, or it is missing any contract column, model
  load fails with a descriptive error listing the problem.
