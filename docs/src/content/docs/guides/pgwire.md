---
title: "PostgreSQL Wire Protocol (pgwire)"
description: "Point psql, JDBC/libpq, Grafana, or Metabase at BifrostQL as if it were a PostgreSQL server. Covers operator setup and auth mapping, the SAFE read-only SQL subset it accepts, the honest-error contract, and the security guarantees that make reads travel the same pipeline as GraphQL."
---

BifrostQL can answer the PostgreSQL frontend/backend protocol on a TCP port, so any
tool that speaks postgres — `psql`, a JDBC/`libpq` client, Grafana's postgres
datasource, Metabase's postgres driver — can **read** your tables as though
BifrostQL were a postgres server. It is a
[protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is postgres,
but every read still executes through the same transformer pipeline as GraphQL, so
tenant isolation, soft-delete, and policy read guards are enforced on the wire.

This is a **read-only, deliberately narrow** front door. It is not a general
PostgreSQL server: it accepts a SAFE subset of `SELECT` and rejects everything
outside it with a clean, honest error. That trade — a small, provable surface over
a large, leaky one — is the whole point.

> Already have a running endpoint and want to validate a real BI tool end to end?
> Jump to the [pgwire BI-Tool Smoke Runbook](/BifrostQL/guides/pgwire-bi-smoke/).

## Enabling the front door

Register the adapter with `AddBifrostPgwire`. Two things are **hard requirements**,
enforced fail-fast at startup so the port can never come up authenticating anonymously
or without TLS:

- an `IPgCredentialStore` — the identity source pg logins authenticate against;
- `PgWireOptions.ServerCertificate` — the cert presented on `SSLRequest`.

```csharp
builder.Services.AddBifrostPgwire(o =>
{
    o.Port = 5432;                                 // default 5432 (the postgres port)
    o.AuthMethod = PgAuthMethod.ScramSha256;       // default; the secret never crosses the wire
    o.MaxConnections = 100;                        // default; N+1th refused with 53300
    o.Endpoint = "/graphql";                       // which BifrostQL endpoint to read; null = the only one
    o.ServerCertificate = LoadServerCertificate(); // REQUIRED — no cert, no start
});

// REQUIRED — the identity source; there is no default registration.
builder.Services.AddSingleton<IPgCredentialStore, MyCredentialStore>();
```

`PgWireOptions`:

| Option | Default | Meaning |
|--------|---------|---------|
| `Port` | `5432` | TCP port the front door listens on. |
| `AuthMethod` | `ScramSha256` | `ScramSha256` (secret never sent) or `Cleartext` (sent over the TLS-wrapped socket). |
| `MaxConnections` | `100` | Concurrent admitted connections; the next is refused cleanly with `53300 too_many_connections`. |
| `ServerCertificate` | *(none — required)* | Cert presented on `SSLRequest`. The port refuses to start without it. |
| `Endpoint` | `null` | Registered BifrostQL endpoint path to read against; `null` selects the single endpoint. |

## Authentication and identity mapping

A pg client presents a **startup username**. `IPgCredentialStore.FindAsync(username)`
resolves it to a `PgLogin(Secret, Principal)`:

- **`Secret`** — the shared secret (API key, client secret, password) the wire
  authentication proves knowledge of. Under SCRAM-SHA-256 it is the PBKDF2 input and
  never crosses the wire; under `Cleartext` it is compared in constant time.
- **`Principal`** — the `ClaimsPrincipal` that login maps to. This is the *candidate*
  identity only: it is still projected through
  [`IBifrostAuthContextFactory`](/BifrostQL/guides/protocol-adapters/#identity-the-auth-context-factory),
  the same fail-closed seam the HTTP GraphQL and binary WebSocket gates use. A
  subject-less or unmapped-issuer principal is rejected there.

**Fail-closed, always.** An unknown username resolves to `null` and authentication
fails — a store must never hand back an ambient or anonymous identity to stand in for
a failed lookup. There is deliberately no default `IPgCredentialStore` registration, so
a deployment can never come up authenticating everyone to nobody. The tenant/policy
claims on the mapped principal are what scope every subsequent read.

TLS is client-initiated per the protocol: the front door answers `SSLRequest` by
upgrading to TLS with `ServerCertificate` (STARTTLS-style, not Kestrel HTTPS). Because
the cert is required, credentials never cross the wire in the clear by misconfiguration.

## The supported SQL subset

The read path accepts an **allowlist** grammar: it recognizes only the shapes below and
rejects everything else. It never rebuilds or forwards your SQL text — it parses to a
typed AST and maps that onto a programmatic `GqlObjectQuery` with every literal bound as
a parameter, so a hostile string is data, never SQL.

**Accepted:**

| Feature | Detail |
|---------|--------|
| Statement | A single `SELECT` (one statement; no trailing `;` second statement). |
| Projection | `SELECT *` or an explicit column list. |
| `FROM` | One table (`schema.table` or bare `table`), optional alias. |
| `JOIN` | **One** `INNER JOIN` on a single equality (`a.x = b.y`), and only where it maps to a **forward (many-to-one), single-column FK, single-link** schema relationship. |
| `WHERE` | Predicates combined with `AND` / `OR` and parentheses. |
| Operators | `=`, `<>`, `!=`, `<`, `<=`, `>`, `>=`, `LIKE`, `IN (…)`, `BETWEEN … AND …`, `IS NULL`, `IS NOT NULL`. |
| Literals | Quoted strings, numbers (incl. a leading `+`/`-` sign), `TRUE`/`FALSE`/`NULL`. |
| `ORDER BY` | One or more columns, `ASC`/`DESC`. |
| `LIMIT` / `OFFSET` | Non-negative integers. |
| Parameters | Extended-protocol `$1`, `$2`, … placeholders in value positions (prepared statements). |

**Rejected** — each with an honest error so the client can tell an unsupported feature
from a typo (see the error contract below):

| Rejected | Error |
|----------|-------|
| Anything that isn't `SELECT` (INSERT/UPDATE/DELETE and other writes) | `42601 syntax_error` — "only SELECT statements are supported" |
| Multiple statements after `;`, trailing tokens, SQL comments | `42601 syntax_error` |
| Non-integer / negative `LIMIT`/`OFFSET`, malformed literal, bad `$N` placeholder | `42601 syntax_error` |
| `SELECT DISTINCT` / `ALL` | `0A000 feature_not_supported` |
| Function calls (e.g. `count(*)`, `now()`) | `0A000 feature_not_supported` |
| Subqueries | `0A000 feature_not_supported` |
| `GROUP BY`, `HAVING` | `0A000 feature_not_supported` |
| `UNION` / `INTERSECT` / `EXCEPT` | `0A000 feature_not_supported` |
| CTEs (`WITH …`) | rejected (not a recognized statement start) |
| Non-`INNER` joins, multi-condition `ON`, a second join | `0A000 feature_not_supported` |
| Composite-FK joins, one-to-many (collection) joins | `0A000 feature_not_supported` |
| Referencing a joined-table column in `WHERE`/`ORDER BY` (joined columns are readable in the `SELECT` list only) | `0A000 feature_not_supported` |

Writes are **not supported at all** — this is a read-only surface. There is no
`INSERT`/`UPDATE`/`DELETE` path; the parser simply never recognizes them as a valid
statement.

### Value formats (extended protocol)

Prepared statements and their parameters/results are **TEXT format only** — the format
every generic prepared-statement driver defaults to. A **BINARY** parameter or result
format code in a `Bind` message is rejected honestly with a clean
`0A000 feature_not_supported` ErrorResponse (binary format is a documented follow-up),
never silently misinterpreted. `CancelRequest` is honored, and the connection cap
returns `53300 too_many_connections`.

## The honest-error contract

Two error paths, and the distinction is a security boundary:

1. **Out-of-subset queries** — a query outside the grammar above yields a *curated,
   user-facing* pg `ErrorResponse` (the SQLSTATE + message shown in the tables above)
   and **the connection survives**. You can correct the query and try again on the same
   session. These messages name the unsupported feature or bad syntax on purpose.

2. **Internal execution faults** — anything that isn't a recognizer error (an execution
   error deeper in the pipeline) is sanitized to a **generic** `XX000 internal_error`
   with a fixed message. Its real text is **never** put on the wire, because it is not
   provably leak-free and could carry raw driver/DB detail. The full exception is logged
   server-side; only the sanitized string crosses the wire.

This is invariant 3 of the adapter security rules (`.claude/rules/protocol-adapter-security.md`):
fail closed toward sanitization. If a chart query fails with a generic "internal error",
that is by design — check the server logs for the real cause.

## Catalog introspection

`psql \dt` / `\d`, and the schema-discovery queries BI tools issue at connect
(`version()`, `information_schema.tables`, `pg_catalog`, …), are answered from a
`DbModel`-derived, **identity-filtered** projection of `pg_catalog` /
`information_schema`. You only see the tables and columns your identity may READ: the
catalog is filtered by the **same** `PolicyEvaluator` read check the query path enforces,
and **fails closed** — a table whose policy cannot be evaluated is omitted, and a
read-denied column is absent from the emulated `pg_attribute` / `information_schema.columns`.
The catalog can never leak the existence of a table you cannot query.

The server reports itself as `PostgreSQL 16.0 (BifrostQL)`.

## Security guarantees

Everything above rolls up to a short list a security reviewer can check:

- **Fail-closed identity.** No anonymous access: no credential store and no server
  certificate means the port does not start; an unknown user or unmapped issuer is
  rejected, never defaulted to an ambient identity.
- **Same pipeline as GraphQL.** Reads execute through `IQueryIntentExecutor`, so every
  registered filter transformer and column read guard applies unconditionally —
  tenant filter, soft-delete, and policy read guards hold on the wire. (Proven by an
  integration test that issues the same query as two identities and gets disjoint,
  tenant-scoped rows.)
- **Parameterized always.** Literals and `$N` values are bound as data through the
  intent executor, never concatenated into SQL.
- **Identity-filtered catalog.** Introspection exposes only what you may read, fail-closed.
- **Sanitized errors.** Out-of-subset → clean pg error, connection survives; internal
  fault → generic `XX000`, no DB internals on the wire.

The narrow subset is itself a guarantee: a small allowlist is far easier to keep safe
than a large surface you must remember to lock down.

## Connecting clients

### psql

```bash
psql "host=localhost port=5432 user=<login> dbname=bifrost sslmode=require"
```

```
\dt                              -- tables you may read (policy-filtered)
\d <table>                       -- columns / types
SELECT * FROM <table> LIMIT 10;  -- only your tenant's rows
```

### JDBC / libpq

Standard postgres connection strings work. Keep SSL on and use TEXT-format prepared
statements (the driver default):

```
jdbc:postgresql://localhost:5432/bifrost?user=<login>&password=<secret>&ssl=true&sslmode=require
```

`libpq`:

```
postgresql://<login>:<secret>@localhost:5432/bifrost?sslmode=require
```

### Grafana / Metabase

Both connect via their PostgreSQL datasource/driver with SSL enabled, introspect the
schema through the emulated catalog, and chart a table within the read-only subset. The
full click-by-click walkthrough — including confirming tenant isolation with a second
login — is the [pgwire BI-Tool Smoke Runbook](/BifrostQL/guides/pgwire-bi-smoke/).

## See also

- [Protocol Adapters (concept)](/BifrostQL/concepts/protocol-adapters/) — why an adapter owns only its wire and codec.
- [Authoring a Protocol Adapter](/BifrostQL/guides/protocol-adapters/) — the intent APIs and conformance kit pgwire is built on.
- [pgwire BI-Tool Smoke Runbook](/BifrostQL/guides/pgwire-bi-smoke/) — validate real Grafana/Metabase end to end.
- [Authentication](/BifrostQL/guides/authentication/) — how principals and claims are mapped.
