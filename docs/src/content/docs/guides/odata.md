---
title: "OData v4 Endpoint"
description: "Point Excel, Power BI, or any OData v4 client at BifrostQL and read your tables as an OData service. Covers opt-in endpoint registration, Bearer/Basic auth, the service document and $metadata, the supported query-option subset ($select/$orderby/$top/$skip/$filter/$count/$expand), server-driven paging, the bounded-resource limits, the deterministic error contract, and the security guarantees that route every read through the same pipeline as GraphQL."
---

BifrostQL can expose your tables through an **OData v4** HTTP endpoint, so any
tool that speaks OData — Excel, Power BI, a Tableau OData connector, an SDK — can
browse your entity sets, read `$metadata`, and run bounded queries against them.
It is a [protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is
OData, but every read still executes through the same transformer pipeline as
GraphQL, so tenant isolation, soft-delete invisibility, and policy read guards are
enforced on the wire — including on `$count` and on `$expand`ed children.

This is a **deliberately narrow, read-only** front door. It is not a full OData
service: it maps a small, well-bounded query surface onto your tables and answers
everything outside that surface with a clean, deterministic error. There are no
mutations (`POST`/`PATCH`/`DELETE`), no `$batch`, no functions or actions, no
delta links, and no OData v2/v3 compatibility.

## Enabling the endpoint

Register the endpoint with `AddODataEndpoint` on your BifrostQL setup, then mount
the middleware with `UseBifrostOData`. The endpoint is opt-in and defaults **off**,
mirroring the pgwire, RESP, and S3 adapters. Enabling it logs a startup warning,
because exposing an OData front door is a posture change worth surfacing, and it
never alters your existing GraphQL/binary routes — the adapter is mounted on its
own branch at `RoutePrefix`.

```csharp
builder.Services.AddBifrostQL(o => o
    // ... your database + module setup ...
    .AddODataEndpoint(odata =>
    {
        odata.Enabled = true;              // opt-in; default false
        odata.RoutePrefix = "/odata";      // where the endpoint listens; default "/odata"
        odata.Realm = "BifrostQL";         // realm label in the 401 WWW-Authenticate challenge
        // odata.DefaultPageSize = 100;    // page size when a request omits $top
        // odata.MaxPageSize = 1000;       // hard ceiling a requested $top is clamped to
        // odata.ContinuationTokenSecret = "…"; // stable HMAC secret so nextLink tokens survive restarts / a scaled fleet
    }));

var app = builder.Build();

// Bearer identity comes from HttpContext.User, so the host's authentication
// middleware must run BEFORE UseBifrostOData.
app.UseAuthentication();

app.UseBifrostOData();   // no-op when the endpoint was not enabled, so it is safe to call unconditionally
```

`AddODataEndpoint` is available on both the single-database (`AddBifrostQL`) and
multi-database option builders, and coexists with the other endpoint wiring —
enabling OData touches no GraphQL or binary service.

### `ODataOptions`

| Option | Default | Meaning |
|--------|---------|---------|
| `Enabled` | `false` | Master gate. Off by default; the endpoint serves nothing until a deployment opts in. |
| `RoutePrefix` | `/odata` | Path prefix the endpoint listens under. The adapter mounts on its own branch here; existing routes are untouched. |
| `Realm` | `BifrostQL` | Label advertised in the `WWW-Authenticate` challenge on a 401. Only a hint for interactive Basic clients; carries no security weight. |
| `Endpoint` | `null` | Which registered BifrostQL endpoint's cached model/connection to read against; `null` selects the single registered endpoint. |
| `DefaultPageSize` | `100` | Page size applied to a collection read when the request supplies no `$top`. Every collection response is bounded — a caller can never pull an unbounded result set in one request. |
| `MaxPageSize` | `1000` | Hard ceiling a requested `$top` is clamped to. A caller asking for more rows than this receives at most this many. |
| `ContinuationTokenSecret` | `null` | HMAC secret the opaque `$skiptoken` continuation token is signed with. When set, `nextLink` tokens survive restarts and resolve across a scaled fleet; when unset, a per-instance random key is used (a startup warning is logged) and tokens live only for the process's lifetime. The token carries position only — it is never the authorization boundary. |
| `ContinuationTokenTtl` | `15 min` | How long a minted continuation token stays valid. An expired token fails closed as a clean `400`, exactly like a tampered one, so a stale `nextLink` can never silently serve a wrong page. |
| `MaxExpandFanout` | `1000` | Upper bound on the number of related rows a single `$expand` navigation may return across the whole page. Exceeding it fails closed as a clean `400` rather than materializing an unbounded fan-out. |

## Authentication

The endpoint authenticates every request before any operation is dispatched, and
projects the resolved identity through the shared
[`IBifrostAuthContextFactory`](/BifrostQL/concepts/protocol-adapters/) — the same
fail-closed identity seam every other transport gate uses. Two credential shapes
are accepted:

- **Bearer** (or any scheme your host's authentication middleware validates
  upstream). The principal is read from `HttpContext.User`, so you must call
  `UseAuthentication()` before `UseBifrostOData()`. An unauthenticated request
  fails closed with `401`.
- **Basic**. Optional. Register an `IODataBasicCredentialStore` to resolve a
  username to its credential and candidate identity; the presented password is
  compared in constant time. A deployment that only accepts Bearer registers no
  store, and a Basic request then fails closed with `401`.

```csharp
// OPTIONAL — only if you want to accept Basic credentials (e.g. from Excel/Power BI).
// A Bearer-only deployment omits this entirely.
builder.Services.AddSingleton<IODataBasicCredentialStore, MyODataBasicCredentialStore>();
```

An unknown username is compared against a fixed decoy secret so it does the same
work as a known one — an attacker cannot distinguish "no such user" from "wrong
password" by timing or response (anti-enumeration). A subject-less principal, an
unmapped OIDC issuer, or a projection that yields an empty user context all fail
closed with `403` — never a degraded or anonymous context. No projection detail
ever reaches the wire; it is logged server-side only.

### Credential caveats for BI tools

The endpoint accepts **Bearer** and **Basic** credentials only. It requires **no
interactive/browser credential flow** of its own — there is no built-in OAuth
authorization-code login, no consent screen, no cookie session. What that means
for the two common clients:

- **Basic** is the path of least resistance for Excel and Power BI: both have a
  native username/password (Basic) option. Register an
  `IODataBasicCredentialStore`, provision a credential, and enter it in the tool.
- **Bearer** works with any client that can attach a static
  `Authorization: Bearer <token>` header (or reach the endpoint through a gateway
  that does). Excel and Power BI do not expose a raw bearer-header field in their
  built-in OData connector, so for those tools prefer Basic, or place a
  header-injecting gateway in front.

Always serve the endpoint over TLS: Basic credentials and bearer tokens both ride
in request headers.

## Routes

All paths are relative to `RoutePrefix` (default `/odata`).

| Route | Returns | Content type |
|-------|---------|--------------|
| `GET /odata` | The OData v4 **service document** — the list of entity sets the caller may read. | `application/json` |
| `GET /odata/$metadata` | The **CSDL/EDMX** schema document (XML), filtered to the caller's visible tables/columns/navigations. | `application/xml` |
| `GET /odata/{EntitySet}` | A bounded **collection read** of one entity set. | `application/json` |

An entity set is named by its table's GraphQL name (matched case-insensitively).
Both the service document and `$metadata` are filtered to only the tables,
columns, and navigations the authenticated caller may READ, using the same policy
gate the query path enforces — so `$metadata` is never an information-disclosure
side channel.

A key predicate (`/odata/Orders(1)`), a navigation segment
(`/odata/Orders/Customer`), and any other addressing shape are **not** part of
this endpoint and answer a clean `501 NotImplemented`.

## Supported query options

| Option | Supported | Notes |
|--------|-----------|-------|
| `$select` | ✅ | Comma-separated property list; validated against visible columns, de-duplicated, order preserved. An unknown or read-denied property is a `400`. |
| `$orderby` | ✅ | `<property> [asc\|desc]`, default ascending. The full primary key is appended ascending as a deterministic tiebreak, so a page boundary never splits rows that compare equal on the requested keys. Composite keys are emitted in full — never a first-column guess. |
| `$top` | ✅ | Clamped to `MaxPageSize`. A non-integer, negative, or overflowing value is a clean `400`. |
| `$skip` | ✅ | Non-negative integer offset. Cannot be combined with `$skiptoken` (the token is the authoritative offset) — doing so is a `400`. |
| `$count` | ✅ | `true`/`false` only (any other literal is a `400`). `true` includes the pipeline-filtered total, computed through the **same** intent (a `COUNT` with the same tenant/soft-delete/`$filter` predicate) — never a side-channel query. |
| `$filter` | ✅ (bounded subset) | See below. |
| `$expand` | ✅ (one level) | See below. |
| `$skiptoken` | ✅ | Server-issued opaque continuation token; see [Server-driven paging](#server-driven-paging). Client-minted values fail closed. |
| Any other `$…` option | ❌ | `$search`, `$compute`, `$apply`, `$format`, `$levels`, … are rejected as `400 Unsupported system query option`. A recognized-but-deferred option answers `501`. |
| Custom (non-`$`) options | ignored | Per the OData spec, non-`$`-prefixed query options are ignored. |

A **duplicated** system query option (e.g. `?$top=1&$top=2`) is a `400`, never a
silent last-write-wins.

### `$filter` subset

The `$filter` parser accepts a deliberately bounded grammar; every other OData
construct is rejected with a deterministic `400`, never partially interpreted.

**Supported:**

- Comparison operators: `eq`, `ne`, `lt`, `le`, `gt`, `ge`
- Boolean logic: `and`, `or`, `not`, with correct precedence and parentheses
- `contains(property, 'value')`
- `property in ('a', 'b', 'c')`
- `property eq null` / `property ne null`

**Not supported (each a `400`):** arithmetic operators, `any`/`all` lambdas, geo
functions, `startswith`/`endswith`/`substringof`, and any other function.

Literal values (numeric, boolean, string, null) are bound as SQL parameters
against the target column type — never interpolated into text. An out-of-range or
malformed literal is a clean `400`.

**Resource bounds** (a malicious `$filter` cannot exhaust the server):

| Cap | Value | Guards against |
|-----|-------|----------------|
| Max nesting depth | `32` | Pathologically nested parentheses / `not` (checked *before* each descent, so it can never reach a stack overflow). |
| Max tokens | `500` | A huge flat `a and a and …` expression. |
| Max `in`-list size | `200` | An oversized `in (…)` list. |

### `$expand`

`$expand` supports exactly **one level** of expansion of a declared schema
relationship, and only navigations the caller may read. Anything richer is
rejected with a deterministic `400` rather than silently served:

- a nested option — the parenthesised form `nav($select=…)` / `nav($expand=…)` /
  `nav($filter=…)`;
- a multi-level path (`a/b`);
- an unknown navigation (not a visible relationship on the entity);
- a self-referential (cyclic) navigation;
- a **composite-key relationship** — the adapter never falls back to the first
  column of a multi-column foreign key. A composite-FK `$expand` is rejected with
  a deterministic `400` (`does not support the composite-key navigation`), not
  silently mis-joined;
- a navigation whose binding key column the caller may not read;
- a navigation named more than once.

Each expansion is executed as its own independent, fully-scoped read intent — so
the expanded child entity gets its own tenant/soft-delete/policy pass. A single
`$expand` navigation that would return more than `MaxExpandFanout` (default
`1000`) related rows across the page fails closed with a `400`, rather than
materializing an unbounded fan-out.

## Server-driven paging

Every collection response is bounded to at most `MaxPageSize` rows. When more rows
match than fit on the page, the response includes an `@odata.nextLink` carrying an
opaque, server-signed `$skiptoken`. Follow the link to fetch the next page; a
client never constructs the token itself.

The token is HMAC-signed and binds the entity set, the query shape
(`$filter`/`$select`/`$orderby`), the effective page size, and a fingerprint of
the caller's identity. A tampered, expired (past `ContinuationTokenTtl`), or
cross-context token — one replayed against a different query, page size, or
identity — fails the integrity check and returns a clean `400`, never a silently
wrong page. Set `ContinuationTokenSecret` to a stable secret if you run more than
one instance or want tokens to survive a restart; the token carries position only
and is never the authorization boundary (tenant/policy scope is re-applied per
request by the pipeline).

## Error contract

Every error is an OData v4 JSON error envelope:

```json
{ "error": { "code": "BadRequest", "message": "..." } }
```

| Status | `code` | When |
|--------|--------|------|
| `400` | `BadRequest` | A malformed request the caller can correct — an unknown/ambiguous property, a non-integer or out-of-range `$top`/`$skip`, a duplicated or unsupported query option, a `$filter` that exceeds a resource bound, a rejected `$expand` shape, or a bad continuation token. |
| `401` | `Unauthorized` | Absent, malformed, or invalid credentials. The response carries a `WWW-Authenticate: Basic realm="…", Bearer` challenge. |
| `403` | `Forbidden` | Authenticated, but the identity is unacceptable — subject-less, unmapped issuer, or an empty projection. |
| `404` | `NotFound` | The addressed entity set does not exist **or is not visible to this identity** — deliberately indistinguishable, so the endpoint is never an existence oracle for tables the caller may not read. |
| `501` | `NotImplemented` | A recognized addressing shape that is out of scope for this endpoint (key predicate, navigation segment). |
| `500` | `InternalError` | An unexpected internal error. The message is a generic sanitized string; full detail is logged server-side only — a Bifrost-internal exception message is never forwarded onto the wire. |

## Security guarantees

The endpoint owns only the OData wire and its codec. Identity and execution both
cross the shared seams, so the same guarantees the GraphQL path makes hold here
without the adapter re-implementing anything:

- **Identity** is projected through `IBifrostAuthContextFactory` — the single,
  fail-closed factory every transport gate shares. Missing or unacceptable
  identity fails closed (`401`/`403`), never a degraded scope.
- **Reads go through `IQueryIntentExecutor`.** The adapter builds **no**
  predicate of its own: it hands a programmatic query plus the caller's user
  context to the intent executor, and the transformer pipeline AND-composes
  tenant isolation, soft-delete, and policy row/column scope onto every read. An
  out-of-scope row simply does not match — the adapter cannot bypass the chain,
  because there is no API that would let it.
- **The unskippable transformers apply to `$count` and `$expand` too.** `$count`
  is a `COUNT` through the same intent with the same predicate, so a caller can
  never count rows they cannot read. Each `$expand`ed child is an independent,
  fully-scoped intent, so it gets its own tenant/soft-delete/policy pass.
- **`$metadata` and the service document are filtered** to only the
  tables/columns/navigations the caller may read, using the same policy gate the
  data path enforces — introspection is not a wider surface than the data.
- **All caller values bind as parameters.** `$filter` literals become bound
  parameters against the target column type; there is no SQL text to concatenate.

For the architectural rationale behind these seams, see
[Protocol Adapters](/BifrostQL/concepts/protocol-adapters/).

## Connecting Excel

Excel's built-in OData Feed connector reads the service document and lets you pick
an entity set as a table.

1. **Data** → **Get Data** → **From Other Sources** → **From OData Feed**.
2. Enter the endpoint root URL, e.g. `https://bifrost.example.com/odata/`.
3. When prompted for credentials, choose **Basic** and enter the username and
   password you provisioned in your `IODataBasicCredentialStore`. (Excel's OData
   connector has no raw bearer-header field; use Basic here, or reach the endpoint
   through a header-injecting gateway.)
4. Excel loads the service document; pick an entity set (for example `Customers`)
   and **Load** or **Transform Data**.

Because the endpoint is read-only, refreshing the query re-reads the current rows;
there is no write-back. Each refresh re-authenticates and re-applies the caller's
tenant/policy scope, so the workbook only ever sees rows that identity may read.

## Connecting Power BI

Power BI Desktop uses the same OData feed mechanism.

1. **Home** → **Get Data** → **OData feed**.
2. Enter the endpoint root URL, e.g. `https://bifrost.example.com/odata/`, and
   choose **Basic** (recommended) rather than **Basic → OData feed** for the whole
   feed.
3. On the credentials prompt, select **Basic** and supply the provisioned
   username/password. Choose the URL level to apply the credential to (the feed
   root is fine).
4. In the Navigator, select the entity sets you want and **Load** or
   **Transform Data**.

For large tables, Power BI follows the endpoint's `@odata.nextLink` automatically
to page through results; the per-page size is bounded by `MaxPageSize`. Use
`$select` in a custom query (or Power Query column selection) to fetch only the
columns you need, and rely on the server's tenant/policy scope rather than
client-side filtering for anything security-relevant.
