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

Draining the outbox — retries, dead-lettering, per-key ordering, and backoff — is
handled by the **dispatcher**, a background component. Until it runs, events
accumulate durably in the outbox table (that is the point of the outbox: the write
path never blocks on delivery). See
[delivery guarantees](/concepts/cdc-outbound-events#delivery-guarantees) for the
at-least-once / per-key-ordering / dead-letter contract.

The dispatcher resolves exactly **one** `IEventSink`. Two ship: the HTTP
**webhook** sink (`Cdc:WebhookUrl`) and the **NATS** sink (`Cdc:NatsUrl`). When a
NATS URL is configured NATS wins; otherwise the webhook registration applies. A
host that configures neither opens no connection and idles.

### The CloudEvents envelope

Each drained outbox row is emitted as a [CloudEvents 1.0](https://cloudevents.io/)
JSON envelope. The captured `payload` travels verbatim as the `data` member; the
other fields are derived from the outbox columns:

| Field | Source | Notes |
|-------|--------|-------|
| `specversion` | — | Always `"1.0"`. |
| `id` | outbox `id` | Also the **idempotency key** consumers dedupe on. |
| `source` | `aggregate` | Qualified source table. |
| `type` | `aggregate` + `op` | `bifrostql.<schema>.<table>.<op>`, e.g. `bifrostql.dbo.orders.insert`. |
| `subject` | row primary key | Composite keys joined with `:`. |
| `time` | `created_at` | RFC3339 UTC (trailing `Z`). |
| `datacontenttype` | — | Always `"application/json"`. |
| `tenant` | `tenant` | **Optional** CloudEvents extension; present only when the row has a tenant. |
| `data` | `payload` | The captured payload JSON, verbatim. |

```json
{
  "specversion": "1.0",
  "id": "4821",
  "source": "dbo.orders",
  "type": "bifrostql.dbo.orders.update",
  "subject": "1007",
  "time": "2026-07-11T22:04:11.0000000Z",
  "datacontenttype": "application/json",
  "tenant": "acme",
  "data": {
    "id": 1007,
    "status": "shipped"
  }
}
```

### Webhook sink

Set `Cdc:WebhookUrl` to the receiver endpoint. The sink HTTP-POSTs the envelope
and signs it:

- **`X-Bifrost-Signature: sha256=<hex>`** — HMAC-SHA256 over the **exact bytes
  sent**, so a receiver's HMAC of the received body matches byte-for-byte. One
  header value is emitted **per active secret** in `webhook-secret`.
- **`Idempotency-Key: <id>`** — the CloudEvents `id`, so at-least-once
  redelivery is de-dupable at the receiver.

**Fail-closed:** with **no** active secret the sink refuses to send (a transient
failure, retried later) rather than deliver an **unsigned** payload.

#### Secret rotation

`webhook-secret` is a comma-separated list. During rotation set both the old and
new secret — `webhook-secret: old-secret,new-secret` — and the sink signs with
**both**, emitting one `X-Bifrost-Signature` value each. A receiver mid-rotation
always verifies against whichever secret it currently trusts. No restart, no
downtime, no dropped deliveries. Once every receiver trusts the new secret, drop
the old value:

```text
:root { webhook-secret: new-secret }
```

### NATS sink

Set `Cdc:NatsUrl` to the NATS server URL. The sink publishes each envelope to a
subject equal to its CloudEvents `type` **verbatim**
(`bifrostql.<schema>.<table>.<op>`) and carries the CloudEvents `id` as the
`Nats-Msg-Id` header, so at-least-once redelivery de-dupes at a JetStream
consumer.

## Kafka sink (follow-up, not yet implemented)

A Kafka `IEventSink` is planned as a further delivery target. It is **not
implemented in this slice** — this section records the mapping the eventual
implementation must follow so it stays consistent with the dispatcher's
per-key ordering guarantee (see the per-key isolation captured in slice 4b).

- **Topic mapping** — one topic per source table:
  `bifrostql.<schema>.<table>` (e.g. `bifrostql.dbo.orders`). The operation
  (`insert`/`update`/`delete`) is **not** part of the topic name (unlike the
  NATS subject, which appends `.<op>`); it travels in the CloudEvents `type`
  header on the message. This keeps every change to a given row on the same
  topic so a single partition can preserve their relative order.
- **Partition key** — the aggregate primary key, i.e. the CloudEvents
  `subject` (the row primary key the envelope builder already carries). Using
  the key as the Kafka partition key routes every event for one row to the
  same partition, which is what preserves per-key order. This matches the
  dispatcher's per-key ordering guarantee from slice 4b — the same key that
  isolates cross-table PK collisions on the drain side is the partition key on
  the wire.
- **Ordering guarantee** — **per-partition FIFO**: all events sharing a
  partition key are delivered to consumers in the order the outbox produced
  them. Events with **different** keys may land on different partitions and are
  therefore **unordered relative to each other** (cross-partition). This is the
  same guarantee the outbox drain provides: order is promised per key, never
  globally.
- **Dedupe** — the CloudEvents `id` carries the de-duplication token, the exact
  counterpart of the webhook `Idempotency-Key` header and the NATS
  `Nats-Msg-Id` header. Because the Kafka **record key** is already the
  partition key (the aggregate PK / CloudEvents `subject`, above), the `id`
  travels as a **message header** rather than the record key — the two roles
  are distinct in Kafka. Delivery is at-least-once; a consumer uses this stable
  `id` to collapse redeliveries of the same event.

No Kafka code ships in this slice; the above is the contract for the follow-up.
