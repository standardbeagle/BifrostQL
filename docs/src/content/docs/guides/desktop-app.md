---
title: "Desktop Database Explorer App"
description: "Browse a local database in a native Photino desktop shell that carries a built-in GraphQL playground, a schema tree, and the same editor the hosted SPA serves."
---

BifrostQL ships a desktop database explorer app built on [Photino.NET](https://www.tryphotino.io/) — a lightweight native window that wraps a web view. Point it at any SQL Server and you get a GraphQL playground with zero setup.

## Install

The desktop app ships as a standalone binary:

```bash
dotnet tool install -g BifrostQL.UI
```

Or build from source:

```bash
pnpm install --frozen-lockfile
pnpm --dir src/BifrostQL.UI/frontend build
dotnet build src/BifrostQL.UI/BifrostQL.UI.csproj
```

This produces a `bifrostui` binary.

## Usage

Pass a connection string directly:

```bash
bifrostui "Server=localhost;Database=mydb;User Id=sa;Password=xxx"
```

Or launch without one and connect through the UI:

```bash
bifrostui
```

### Options

| Flag | Short | Description |
|------|-------|-------------|
| `--port` | `-p` | Port for the embedded server (default: 5000) |
| `--expose` | `-e` | Bind to `0.0.0.0` instead of localhost |
| `--headless` | `-H` | Server only, no desktop window |
| `--vault` | `-V` | Path to encrypted vault file |

### Headless mode

Run BifrostQL as a standalone server without the desktop window:

```bash
bifrostui "Server=localhost;Database=mydb;..." --headless --port 8080
```

This gives you the same GraphQL endpoint and playground at `http://localhost:8080/graphql` and `http://localhost:8080/graphiql`, plus the connection management API -- useful for remote servers or Docker containers.

## Credentials and vault

Current desktop builds do not accept password-bearing connection strings through `/api/connection/set`. Passwords stay in the native host and are stored in an encrypted local vault. The UI can create vault entries through the Photino native bridge, or you can manage them from the CLI:

```bash
bifrostui vault add prod --provider postgres --host db.example.com --database app --username appuser
bifrostui vault list
bifrostui vault remove prod
bifrostui vault export
```

Use `--password-stdin` for automation:

```bash
printf '%s\n' "$DB_PASSWORD" | bifrostui vault add prod --provider postgres --host db.example.com --database app --username appuser --password-stdin
```

Vault entries may include SSH tunnel settings and WordPress tags. Saved entries appear on the welcome screen without exposing passwords to the renderer.

### SQL Server certificate validation

Vault-backed SQL Server connections encrypt (`Encrypt=Mandatory`) **and validate the server's certificate**. A server presenting a self-signed or internally issued certificate that your machine does not trust will therefore fail to connect, with an error naming the setting below.

To connect anyway, waive validation for that one entry:

```bash
bifrostui vault add internal --provider sqlserver --host sql.internal --database app --username appuser --trust-server-certificate
```

In the desktop UI this is the "Trust Server Certificate" checkbox on the SQL Server connection form.

The waiver accepts **any** certificate the server presents, so the connection is encrypted but the server's identity is unverified — anyone able to sit on the network path can terminate the TLS session themselves and read the credentials and query traffic. Use it only on a network path you trust. Connecting with such an entry logs a warning naming the server.

It applies per entry and is off unless set, including for entries saved before the option existed. The waiver is ignored where it cannot mean anything: with `--ssl-mode disable` there is no certificate to trust, and `--ssl-mode strict` exists precisely to validate one.

A connection string passed straight to `bifrostui` is used verbatim, so it carries whatever `TrustServerCertificate` you put in it and nothing is added on your behalf — see [TrustServerCertificate](/reference/configuration/#trustservercertificate) for what accepting it costs.

## What it does

The desktop app bundles a full BifrostQL server inside a native window:

- **Provider-aware connection flow** -- SQL Server, PostgreSQL, MySQL, and SQLite connection forms with per-provider validation.
- **Encrypted credential vault** -- Saved servers are listed through `/api/vault/servers`; connecting uses `/api/vault/connect` so credentials stay server-side.
- **SSH and WordPress helpers** -- Optional SSH tunnels plus WordPress database credential discovery through `wp-cli`.
- **GraphQL playground/editor** -- Built-in editor at `/graphiql` and the React table editor loaded from the app shell.
- **SQLite quickstarts** -- Create local demo databases through `/api/database/create-quickstart` and stream progress with Server-Sent Events.
- **Transport toggle** -- Header toggle switches the editor between HTTP and binary WebSocket transports. All editor GraphQL queries — schema, data grid, mutations, stats — route through the selected transport.
- **Health check** -- `/api/health` reports server status and connection state.

## Navigator panes

The window is more than a playground — it's a full database navigator. The editor shell switches between several panes, each driven by the live schema:

- **Data grid** — browse and edit any table. Responsive table-list and header that reflow on narrow windows, in-cell text selection (drag to copy without navigating into a row), and **locale-aware value formatting**: dates, relative times ("4 hours ago"), grouped numbers, and percentages render through native `Intl`, with the exact raw value always available on hover. Format per column with the `display-format` metadata key (`date`, `datetime`, `time`, `relative`, `number`, `percent`, `raw`).
- **Opt-in table stats** — the desktop app shows per-table row-count bars that scale with the column width. Stats are off by default in the embeddable editor (zero extra queries) and turned on in the desktop build.
- **[Visual Query Builder](/BifrostQL/concepts/visual-query-builder/)** — an Access-style designer: pick tables, let FK auto-join wire the relationships (composite-aware), set criteria and sort in a grid, preview the parameterized SQL, and run it over the in-process bridge — no SQL required.
- **Form builder** — an Access-style pane for laying out single-record data-entry forms: pick a table, choose control types per column, set labels, required/read-only flags, a 1–4 column grid layout, and see a live preview.
- **Many-to-many picker** — attach and detach junction-table links directly, with optional payload-column editing on the join row.
- **Raw SQL console** — arbitrary SQL (including DML/DDL) over the same in-process execution path the builder uses.

### Why these panes are desktop-only

The query builder, form builder and SQL console run over the Photino bridge, in
process, and never touch the HTTP/GraphQL surface. That is what lets them execute
arbitrary SQL with no authentication of their own: inside the desktop app the only
possible caller is the window the host itself opened.

That also means they do not exist in a browser pointed at a headless host — there
is no `window.external` to talk to — which left them unreachable from the
end-to-end suite. `--enable-http-bridge` exposes the same bridge handlers over
loopback HTTP so the panes work headless, and the suite passes it when it starts
its server.

**It is a testing flag, not a deployment option.** Enabling it puts a surface that
runs arbitrary SQL against the active connection onto a socket, which removes the
assumption the bridge's design rests on. It is off by default, binds only where
the UI host already binds, and logs a warning at startup. Do not enable it on a
host anyone else can reach.

## Theming the editor

The data editor ships as an embeddable React component (see [Embeddable Data Editor](/BifrostQL/guides/embedded-editor/)) and is themed through a **CSS custom-property contract** layered with `@layer` so host styles win without specificity fights. Override any of the `--ui-*` tokens — `--ui-background`, `--ui-foreground`, `--ui-primary`, `--ui-border`, `--ui-accent`, `--ui-destructive`, and their `-foreground` pairs — to re-skin every grid and form. The desktop app uses this contract to apply its Norse-industrial dark palette.

## About & diagnostics

An **About / diagnostics** panel (welcome-footer link and editor header button) reports the SPA, host, and engine versions side by side — flagging a mismatch that usually means a stale frontend build — plus the .NET runtime, OS, and current connection state. `/api/health` and `/api/diagnostics` expose the same data for scripts and monitoring.

## Architecture

The app runs an embedded ASP.NET Core server on localhost and opens a Photino native window pointed at it. The server hosts both the BifrostQL GraphQL endpoint and a React-based frontend.

```
bifrostui
├── ASP.NET Core server (localhost:5000)
│   ├── /graphql          — BifrostQL GraphQL endpoint
│   ├── /bifrost-ws       — Binary WebSocket endpoint
│   ├── /graphiql         — GraphQL playground
│   ├── /api/providers    — Available database providers
│   ├── /api/connection/* — Connection testing
│   ├── /api/vault/*      — Server-side credential vault
│   ├── /api/ssh/*        — SSH tunnel and WordPress discovery helpers
│   ├── /api/database/*   — SQLite quickstart database creation
│   └── /api/health       — Health check
└── Photino native window
    └── Loads http://localhost:5000
```

Kestrel is configured with larger request header limits (128KB) to accommodate auth tokens.

## Quickstart database templates

The `/api/database/create-quickstart` endpoint creates SQLite databases from built-in schemas and accepts a `schema` plus `dataSize`.

| Template | Tables | Description |
|----------|--------|-------------|
| `northwind` | Categories, Products, Customers, Orders, OrderDetails | Classic Northwind with foreign key relationships |
| `adventureworks-lite` | Departments, Employees, Shifts, EmployeeDepartmentHistory | HR-style schema with history tracking |
| `simple-blog` | Users, Posts, Comments, Tags, PostTags | Blog with many-to-many tag relationships |

Database creation streams progress via Server-Sent Events, reporting each stage with percentage updates. The legacy `/api/database/create` endpoint is disabled because it accepted password-bearing connection strings over HTTP.

## Frontend assets

The desktop app serves static assets from `src/BifrostQL.UI/wwwroot`, but those files are generated Vite output and are not tracked in git. Change the React source under `src/BifrostQL.UI/frontend/src`, then rebuild with:

```bash
pnpm --dir src/BifrostQL.UI/frontend build
```
