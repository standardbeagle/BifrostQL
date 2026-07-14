---
title: "pgwire BI-Tool Smoke Runbook"
description: "Manually verify that Grafana, Metabase, and psql can connect to the BifrostQL PostgreSQL wire-protocol endpoint over a real network socket, introspect the schema, and chart a table — the end-to-end path the automated tests deliberately do not fake."
---

BifrostQL exposes a PostgreSQL wire-protocol front door (pgwire) so any tool that
speaks the postgres frontend/backend protocol — psql, Grafana's postgres
datasource, Metabase's postgres driver — can read your tables as if BifrostQL
were a postgres server. Reads travel the same transformer pipeline as GraphQL,
so tenant isolation, soft-delete, and policy read guards are enforced on the
wire.

This runbook is the **manual** end-to-end smoke: it points **real** Grafana and
Metabase at a running BifrostQL pgwire endpoint and charts a table. It is
deliberately **not** part of `dotnet test` — the automated gate reproduces the
*wire query sequences* these tools issue in-process (see
`PgWireBiToolWireQueryTests`), but a genuine cross-process connection to real
Grafana/Metabase needs external services and cannot honestly run headless in CI.
Run this by hand when validating a release.

> **Honesty note.** The automated suite proves our catalog and read path answer
> the introspection + data queries these drivers send, and that data stays
> tenant-scoped. It does **not** prove a real Grafana/Metabase build connects —
> that is what this runbook is for. If you have not run the steps below, do not
> claim the BI-tool smoke passed.

## Prerequisites

- A BifrostQL host with the pgwire front door enabled (`AddBifrostPgwire`), a
  configured `IPgCredentialStore` login, and `PgWireOptions.ServerCertificate`
  set (the port refuses to start without TLS).
- The endpoint reachable on a host/port, e.g. `localhost:5432`.
- A login whose principal carries a tenant claim (so tenant filtering applies).
- Docker (for the Grafana/Metabase containers) or local installs.

Example server wiring:

```csharp
builder.Services.AddBifrostPgwire(o =>
{
    o.Port = 5432;
    o.AuthMethod = PgAuthMethod.ScramSha256;
    o.Endpoint = "/graphql";                     // the BifrostQL endpoint to read
    o.ServerCertificate = LoadServerCertificate(); // required — no TLS, no start
});
```

## Step 1 — psql (fastest sanity check)

```bash
psql "host=localhost port=5432 user=<login> dbname=bifrost sslmode=require"
```

At the prompt:

```
\dt                       -- lists the tables you may read (policy-filtered)
\d <table>                -- describes columns / types
SELECT version();         -- reports the BifrostQL server-version banner
SELECT * FROM <table> LIMIT 10;   -- returns ONLY your tenant's rows
```

**Expected:** `\dt` lists your readable tables; `SELECT * FROM <table>` returns
only rows for the tenant your login maps to. Issue the same query as a login in a
different tenant and confirm the row sets are disjoint.

## Step 2 — Grafana

```bash
docker run -d --name grafana -p 3000:3000 grafana/grafana:latest
```

1. Open `http://localhost:3000` (admin/admin).
2. **Connections → Add new connection → PostgreSQL.**
3. Host `host.docker.internal:5432`, Database `bifrost`, User `<login>`,
   Password `<secret>`, TLS/SSL Mode `require`.
4. **Save & test** → expect *"Database Connection OK"*.
5. **Explore** → pick the datasource → the table dropdown lists your tables
   (from `information_schema`) → choose one → **Table** or **Time series**
   visualization → run.

**Expected:** the table/column pickers populate from the schema, and the chart
renders only your tenant's rows. Repeat with a second-tenant login → disjoint
data.

## Step 3 — Metabase

```bash
docker run -d --name metabase -p 3001:3000 metabase/metabase:latest
```

1. Open `http://localhost:3001`, complete first-run setup.
2. **Add a database → PostgreSQL.** Host `host.docker.internal`, Port `5432`,
   Database `bifrost`, User `<login>`, Password `<secret>`, **Use a secure
   connection (SSL)** on.
3. Save → Metabase runs its JDBC **sync** (introspects
   `information_schema` / `pg_catalog`).
4. **Browse data → the synced database → a table** → Metabase auto-renders a
   table; use **Visualization** to chart a numeric column.

**Expected:** sync completes and lists your tables/columns; opening a table shows
only your tenant's rows; a chart of a numeric column renders.

## What to record

For a release smoke, note for each tool: connected (y/n), schema introspection
populated (y/n), chart rendered (y/n), tenant isolation confirmed with a second
login (y/n). Anything that fails is a real defect — do not paper over it.

## Troubleshooting

- **Connection refused / no TLS:** the port refuses to start without
  `ServerCertificate`; a self-signed cert is fine for a smoke, with the client
  set to `sslmode=require` (not `verify-full`).
- **A dropdown/introspection query errors:** the tool issued a catalog query
  outside the emulated subset. Capture the exact SQL (Grafana query inspector /
  Metabase admin logs) and file it — the catalog responder (`PgCatalogResponder`)
  is where new introspection shapes are added.
- **A chart query errors with a generic "internal error":** by design the wire
  sanitizes execution faults; check the server logs for the real cause.
