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
The full pipeline ships: the **metadata contract**, the **transactional writer**
(across all write paths — single-row, batch, and nested TreeSync mutations each
write an event row **in the same transaction** as the data change), the
background **dispatcher** that drains the outbox, and two delivery **sinks**
(HTTP webhook and NATS). Each drained row is emitted as a
[CloudEvents 1.0](https://cloudevents.io/) envelope. See
[Emitting Change Events](/guides/cdc-events) for the sink configuration and the
envelope shape.
:::

:::caution[Only mutations through the Bifrost pipeline are captured]
Events are emitted **only** for writes that flow through Bifrost's mutation
pipeline — GraphQL mutations and protocol adapters routed through the mutation
executor. Bifrost is **not** log-based CDC:

- **Out-of-band SQL writes emit nothing.** Direct database writes from other
  applications, bulk loads, and manual SQL do **not** produce events — they never
  touch the pipeline that writes the outbox row.
- **DB-native triggers and log-based replication are out of scope.** Bifrost does
  not read the transaction log (SQL Server CDC, Postgres logical replication / WAL)
  and does not install database triggers.

A consumer that assumes *every* row change produces an event will silently miss
every change made outside Bifrost. If a table is written both through Bifrost and
out-of-band, only the Bifrost writes are captured.
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
- **`webhook-secret`** — comma-separated signing secret(s) the webhook sink uses
  to HMAC-sign deliveries. Multiple values enable zero-downtime
  [secret rotation](/guides/cdc-events#secret-rotation).

### Payload modes

| Mode | Captured in `payload` |
|------|-----------------------|
| `full` | The entire post-image of the row. |
| `changed` | Only the columns that changed, plus the primary key. |
| `keys` | Primary key columns only (consumer re-reads the row). |

`keys` is the smallest and safest for sensitive tables; `full` is the most
convenient for consumers that cannot re-query. `changed` is a middle ground for
audit-style feeds.

:::caution
The writer runs **after the write but inside the same transaction**, so its event
commits atomically with the change and captures the database-generated primary key
on an INSERT. It reflects the mutation's *write inputs* (plus that generated key),
not a re-read of every stored column. In practice: `keys` captures the primary
key; `changed` captures the columns the mutation writes plus the key; and `full`
currently equals `changed` (for an INSERT that is the whole new row including its
generated key; for an UPDATE it is the changed columns). A true full post-image
re-read of DB-defaulted columns is a planned refinement.
:::

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

## Delivery guarantees

The dispatcher drains undelivered, non-dead outbox rows in `id` order and stamps
`dispatched_at` on success. Its contract:

- **At-least-once.** A crash after a sink accepts an event but before
  `dispatched_at` is stamped causes that event to be redelivered on the next pass.
  Consumers **dedupe on the CloudEvents `id`** (the outbox row `id`), which is
  stable across redeliveries.
- **Per-key ordering.** Events for the same `(aggregate, primary key)` are
  delivered in `id` order. A transient failure head-of-line-blocks **only that
  key** — other keys keep flowing, so one slow row never stalls the whole stream.
- **Dead-letter.** After the delivery-attempt budget (default **5**) a row is
  flagged `dead`: never re-dispatched, never deleted. It stays in the outbox for
  operator inspection. `aggregate`, `op`, and attempt count are logged; the
  payload and secrets never are.
- **Backoff.** Between retry passes the dispatcher waits a bounded exponential
  backoff with equal jitter (floor 1s, ceiling 5m).

## Subscription routing

A model-level **subscription** filters what the sink receives. Filtering happens
in routing, **before** any sink sees the event.

- **Opt-in.** With **no** `subscription-*` key, every event is delivered
  (deliver-all — the default, unchanged). Declaring any `subscription-*` key
  activates the subscription.
- **Fail-closed table allow-list.** When a subscription is active, an event is
  delivered only if its `aggregate` is listed in `subscription-tables`. An
  **empty** allow-list (with a subscription otherwise active) delivers
  **nothing** — never everything.
- **Tenant scoping.** A subscription bound with `subscription-tenant` receives
  only rows whose outbox `tenant` equals that id. A null- or unknown-tenant row is
  **never** delivered to a tenant-bound subscription.
- **Redaction.** Columns named in `subscription-redact` are stripped from the
  payload before the sink sees them. A **primary-key column is never stripped**,
  even if listed — removing it would corrupt the CloudEvents `subject` and
  consumer identity.

Unknown `subscription-*` keys and a `subscription-tables` entry naming a
non-existent table fail at **model load**, not at delivery time.

## Validation

Misconfiguration fails fast at model load rather than aborting a real write later:

- An unrecognized `emit-events` operation, `event-sink`, or `event-payload` value
  is rejected (a typo would otherwise silently never emit).
- If any table sets `emit-events` but no `outbox-table` is configured, or the
  named outbox table does not exist, or it is missing any contract column, model
  load fails with a descriptive error listing the problem.
