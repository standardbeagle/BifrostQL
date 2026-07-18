---
title: "gRPC Endpoint"
description: "Expose your tables over gRPC (HTTP/2) with server reflection, so any gRPC client can discover and read them with no local .proto files. Covers opt-in registration, HTTP/2 + TLS, bearer-metadata auth, the grpcurl/reflection client workflow, the DbModel-to-proto3 type map, the field-number manifest lifecycle, the read-surface limits, the opt-in write RPCs, and the RPC / operator / status matrices — every call routed through the same pipeline as GraphQL."
---

BifrostQL can expose your tables through a **gRPC (HTTP/2)** front door, so any
gRPC client — grpcurl, a generated stub, an SDK — can discover your tables via
server reflection and read them with strongly-typed messages. It is a
[protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is gRPC, but
every read still executes through the same transformer pipeline as GraphQL, so
tenant isolation, soft-delete invisibility, and policy read guards are enforced
on the wire — including on what server reflection is even allowed to show you.

The message and service **shape** is generated at runtime from your live
`DbModel`; the field **numbers** are pinned by a checked-in manifest so the wire
contract survives schema drift. There is no `.proto` file compiled into the
server and none required by clients — see the
[gRPC Schema Contract ADR](/BifrostQL/concepts/grpc-schema-contract/) for why.

This is a **deliberately narrow** front door. It maps a bounded read surface
(`Get` / `List` / `Stream`) — plus opt-in `Insert` / `Update` / `Delete` — onto
your tables and answers everything outside that surface with a clean gRPC status.
There is **no gRPC-Web, no client-streaming or bidirectional streaming, no
arbitrary SQL, and no cross-RPC transactions** (see [Non-goals](#non-goals)).

## Enabling the endpoint

Register the adapter with `AddBifrostGrpc` on your service collection, then map
its routes with `MapBifrostGrpc`. The endpoint is opt-in and defaults **off**,
mirroring the OData, pgwire, RESP, and S3 adapters. `AddBifrostGrpc` binds a
**dedicated HTTP/2 Kestrel listener** on the configured port and registers the
dynamic dispatch service plus the identity-filtered reflection service; a bind
failure or misconfiguration **aborts host startup** (fail-fast) — the adapter
never comes up half-configured.

```csharp
builder.Services.AddBifrostQL(o => o
    // ... your database + module setup ...
);

builder.Services.AddBifrostGrpc(grpc =>
{
    grpc.Port = 5090;                 // dedicated HTTP/2 listener port; default 5090
    // grpc.Endpoint = null;          // which registered BifrostQL endpoint to read; null = the single one
    // grpc.RequireTls = true;        // require TLS; then TlsCertificatePath must resolve
    // grpc.TlsCertificatePath = "/etc/certs/grpc.pfx";
    // grpc.MaxStreamRows = 10_000;   // hard ceiling on rows a Stream/List may emit
    // grpc.ListPageSize = 1_000;     // unary List page size (clamped to MaxStreamRows)
    // grpc.PageTokenSecret = "…";    // stable HMAC secret so page tokens survive restarts / a scaled fleet
    // grpc.EnableWrites = false;     // GLOBAL opt-in for Insert/Update/Delete; default false
});

var app = builder.Build();

// Bearer identity is read off the HTTP/2 request, so the host's authentication
// middleware must run before the gRPC routes are reached.
app.UseAuthentication();

app.MapBifrostGrpc();   // maps the dynamic Get/List/Stream service + reflection
app.Run();
```

### `GrpcWireOptions`

| Option | Default | Meaning |
|--------|---------|---------|
| `Port` | `5090` | TCP port the dedicated HTTP/2 listener binds. Out of `1..65535` is a fail-fast startup error; a bind failure aborts startup. |
| `Endpoint` | `null` | Which registered BifrostQL endpoint's cached model/connection to read against. `null` selects the single registered endpoint; with several registered it is required. |
| `RequireTls` | `false` | When `true`, `TlsCertificatePath` must resolve to a readable certificate file or startup aborts. Cleartext (h2c) is the default for local / in-proxy deployments; production should terminate TLS. |
| `TlsCertificatePath` | `null` | Path to the PKCS#12/PEM certificate used when `RequireTls` is set. |
| `MaxStreamRows` | `10000` | Hard upper bound on rows a server-streaming `List`/`Stream` may emit. The read intent's limit is clamped to this, so a full-table stream is always bounded by config. Must be positive. |
| `ListPageSize` | `1000` | Page size a unary `List` returns when the request omits `page_size`. Clamped to `MaxStreamRows`. Must be positive. |
| `PageTokenSecret` | `null` | HMAC secret keying the opaque `next_page_token`. When set, page tokens survive restarts and resolve across a scaled fleet; when unset, a per-instance random key is used (a startup warning is logged) and tokens live only for the process's lifetime. The token carries position only — it is never the authorization boundary. |
| `PageTokenTtl` | `15 min` | How long a page token stays valid before it fails closed exactly like a forged one. |
| `EnableWrites` | `false` | Global opt-in for the write RPCs (`Insert`/`Update`/`Delete`). Off by default — a read-only front door. See [Write RPCs](#write-rpcs-opt-in). |
| `Manifest` | empty | The field-number manifest. Empty allocates deterministic numbers from the live schema; a checked-in manifest keeps numbers stable across schema drift. See [Field-number lifecycle](#field-number-lifecycle). |

## HTTP/2 and TLS

gRPC requires HTTP/2 for its framing and trailers, so the adapter binds its own
listener speaking HTTP/2 only — it does not share your GraphQL/HTTP port.
Cleartext HTTP/2 (**h2c**) is the default and is appropriate when a reverse proxy
or service mesh terminates TLS in front of BifrostQL. To terminate TLS at
BifrostQL itself, set `RequireTls = true` and point `TlsCertificatePath` at a
readable certificate; a missing or unreadable certificate aborts startup rather
than silently downgrading to cleartext.

## Authentication

Every RPC resolves identity **before** any intent is built, through the shared
fail-closed [`IBifrostAuthContextFactory`](/BifrostQL/concepts/protocol-adapters/)
— the same identity seam OData, MCP, and S3 use. The caller presents a bearer
credential in the gRPC `authorization` request metadata:

```
authorization: Bearer <token>
```

The adapter does not decide claim mapping itself; it projects the credential
through the shared factory and **fails closed** on anything short of a valid,
mapped identity:

- a **missing / anonymous** credential — the projection is empty — is rejected;
- an **unmapped issuer** or a **subject-less** principal — the projection throws
  — is rejected;
- an **abusive / oversized** `authorization` value (beyond an 8 KB cap) is
  rejected with fixed work, before any parse.

Every one of these surfaces the **same** `UNAUTHENTICATED` status with a fixed,
non-revealing message; the real cause is logged server-side only, so the wire
cannot be used to enumerate which issuers or users exist. There is no
"allow anonymous" switch — an unauthenticated call never reaches the executor
with a permissive identity, and if it somehow did, the pipeline's tenant/policy
transformers scope an empty identity to nothing.

## Discovering the schema with server reflection

The adapter serves **server reflection** (`grpc.reflection.v1alpha.ServerReflection`),
so clients need **no local `.proto` files** — they learn the message and service
shapes from the running server. Reflection is **filtered per caller** by the same
read policy the query path enforces: a table, RPC, or column the caller may not
read is **absent** from what reflection returns, and a denied symbol is reported
as `NotFound` — identical to a genuinely unknown symbol, so absence never leaks
existence. (This is a bespoke identity-filtered reflection service, not the
stock global-descriptor reflection.)

List the services and describe the surface with
[grpcurl](https://github.com/fullstorydev/grpcurl):

```bash
# List services (plaintext h2c). Drop -plaintext when TLS is terminated at the server.
grpcurl -plaintext -H 'authorization: Bearer <token>' localhost:5090 list

# → bifrostql.BifrostQuery
#   grpc.reflection.v1alpha.ServerReflection

# List the RPCs on the query service
grpcurl -plaintext -H 'authorization: Bearer <token>' localhost:5090 list bifrostql.BifrostQuery

# Describe a message
grpcurl -plaintext -H 'authorization: Bearer <token>' localhost:5090 describe bifrostql.UsersRow
```

The single service is **`bifrostql.BifrostQuery`** (package `bifrostql`, service
`BifrostQuery`), and it carries one set of RPCs per visible table.

### Reading a row and a page

```bash
# Get one row by its full primary key (all key columns; composite keys included)
grpcurl -plaintext -H 'authorization: Bearer <token>' \
  -d '{"id": 42}' \
  localhost:5090 bifrostql.BifrostQuery/GetUsers

# List a page with a JSON filter, an order_by, and a page size
grpcurl -plaintext -H 'authorization: Bearer <token>' \
  -d '{"filter": "{\"age\": {\"_gte\": 18}}", "order_by": "name asc", "page_size": 100}' \
  localhost:5090 bifrostql.BifrostQuery/ListUsers

# The response carries "rows" and, when a full page came back, a "next_page_token".
# Pass it back as page_token to fetch the next page.

# Stream all matching rows (server-streaming; bounded by MaxStreamRows)
grpcurl -plaintext -H 'authorization: Bearer <token>' \
  -d '{"filter": "{\"active\": {\"_eq\": true}}"}' \
  localhost:5090 bifrostql.BifrostQuery/StreamUsers
```

## Generating a client (no local protos)

Because the shape is discovered from the live database, there is no checked-in
`.proto` to distribute. Two workflows, both reflection-first:

1. **Reflection at call time (no codegen).** grpcurl, [Postman], [Kreya], and
   most gRPC GUIs speak reflection directly — point them at the port with a
   bearer token and they build request forms from the live descriptors.

2. **Export descriptors, then compile a stub.** Have grpcurl write the visible
   descriptor set to a file via reflection, then feed that `FileDescriptorSet`
   straight to `protoc` (it accepts a descriptor set as input — no `.proto`
   source needed):

   ```bash
   # Fetch the caller-visible descriptor set over reflection
   grpcurl -plaintext -H 'authorization: Bearer <token>' \
     -protoset-out bifrostql.protoset \
     localhost:5090 describe bifrostql.BifrostQuery

   # Generate a client stub from the descriptor set (example: Go)
   protoc --descriptor_set_in=bifrostql.protoset \
     --go_out=. --go-grpc_out=. \
     bifrostql.proto
   ```

Because reflection is identity-filtered, the exported descriptor set contains
**only the tables and columns that credential may read** — the generated client
cannot even name a column its caller is denied.

[Postman]: https://learning.postman.com/docs/sending-requests/grpc/grpc-request-interface/
[Kreya]: https://kreya.app/

## Type mapping (DbModel → proto3)

Each column's effective SQL type maps to a proto3 wire type. Two decisions are
load-bearing: **`decimal`/`numeric`/`money` carry exact decimal text as a proto
`string`** (never a lossy `double`), and **`bigint` stays `int64`** (exact on the
wire).

| SQL type | proto3 type |
|----------|-------------|
| `int`, `smallint`, `tinyint` | `int32` |
| `bigint` | `int64` |
| `decimal`, `numeric`, `money`, `smallmoney` | `string` (canonical decimal text) |
| `float` | `double` |
| `real` | `float` |
| `bit` | `bool` |
| `datetime`, `datetime2`, `datetimeoffset`, `smalldatetime`, `date` | `google.protobuf.Timestamp` |
| `binary`, `varbinary`, `image`, `rowversion`, `timestamp` | `bytes` |
| anything else | `string` |

**Nullable presence.** A nullable column is emitted with proto3 **explicit
presence** (the `optional` keyword), so a database `NULL` is wire-distinguishable
from a typed default (`0`, `""`, `false`). Non-nullable columns use implicit
presence.

## Field-number lifecycle

Field numbers come from the checked-in **manifest**, never from a column's
ordinal position in the current database read — so the wire contract survives
column reordering, addition, and removal. At startup the full model is reconciled
against the manifest once, and dynamic dispatch and identity-filtered reflection
share the result, so they can never disagree on a number.

| Schema change | Field-number behavior |
|---------------|----------------------|
| **Additive column** | Gets the next free number; **every existing number is preserved.** |
| **Column removal** | Its number moves to `reserved` and its name to `reservedNames` — **never reused.** |
| **Column rename** | Treated as remove + add: old number reserved, renamed column gets a **new** number. |
| **Incompatible type change** on an existing number | Generation **fails** at startup — a field number's wire type cannot change. Reserve the old number and allocate a new one. |
| **Column hidden by a profile** | Absent from that caller's projected surface, but its number stays reserved — a hidden column never frees its number. |
| **No structural change** | Numbers are byte-stable across builds regardless of DB column read order. |

An empty manifest (the default) allocates deterministic numbers from the live
schema each run; check in a manifest to freeze them. A **table with no primary
key fails generation at startup** — a `Get` RPC has no way to identify a row
without one. See the
[gRPC Schema Contract ADR](/BifrostQL/concepts/grpc-schema-contract/) for the
full rationale.

## Read surface: filter, sort, and paging

`List` and `Stream` share **one** request shape and **one** compiler, so the same
request yields identical, identically-ordered rows on both. Every read request
carries four fields:

| Field | Type | Meaning |
|-------|------|---------|
| `filter` | `string` | A JSON predicate (see below). Omitted / empty means no filter. |
| `order_by` | `string` | Comma-separated `<field> [asc\|desc]` clauses; direction defaults to `asc`. |
| `page_size` | `int32` | Rows per page. `0` or negative means the endpoint default; a positive value is clamped to the maximum. |
| `page_token` | `string` | Opaque continuation token from a previous page's `next_page_token`. |

### Filter JSON

The `filter` is a JSON object of the same shape the GraphQL/OData paths use.
Every literal binds as a SQL parameter (never interpolated), and field and
operator names are validated against the caller's **identity-visible** columns
and the operator set below — an unknown or unreadable field is a clean
`INVALID_ARGUMENT`, never an oracle.

```json
{ "and": [
  { "age":  { "_gte": 18 } },
  { "name": { "_contains": "sam" } },
  { "status": { "_in": ["active", "trial"] } }
] }
```

Supported operators:

| Operator | Meaning | Notes |
|----------|---------|-------|
| `_eq`, `_neq` | equal / not equal | |
| `_lt`, `_lte`, `_gt`, `_gte` | ordered comparisons | |
| `_contains` | substring match | string columns only |
| `_in` | value in list | 1..200 values |
| `_between` | inclusive range | exactly 2 values |
| `_null` | is / is not null | boolean argument |
| `and`, `or` | boolean combiners | arrays of filter nodes |

The filter compiler is AND-composed downstream with the pipeline's
tenant/soft-delete/policy filters — it can neither replace nor bypass them.

### Sorting and deterministic paging

`order_by` is a comma-separated list, e.g. `"name asc, created desc"`. The
compiler appends the table's **full** primary key ascending as a tiebreak, so a
page boundary never splits rows that compare equal on the requested keys.
Composite keys are emitted in full — never reduced to a first column.

### Limits

Every untrusted bound is enforced **before** it is honored; an over-cap request
fails closed as a clean `INVALID_ARGUMENT`, never an unbounded recursion or
allocation.

| Limit | Value |
|-------|-------|
| Filter nesting depth (`and`/`or` tree) | 32 |
| Filter predicates + combiners, total | 500 |
| `_in` / `_between` list size | 200 |
| Caller-supplied sort keys | 32 |
| Page-token length the adapter will decode | 4096 chars |
| Default page size | `ListPageSize` (1000) |
| Maximum page size / stream rows | `MaxStreamRows` (10000) |

## Write RPCs (opt-in)

Writes are **off by default**. Exposing `Insert` / `Update` / `Delete` requires
**both** gates:

1. **Global switch** — `GrpcWireOptions.EnableWrites = true`. Enabling it logs a
   startup **warning** (a posture change worth surfacing).
2. **Per-table allow-list** — the table carries the metadata key
   **`grpc-write: enabled`**.

A table failing **either** gate generates no mutation RPCs at all — they are
absent from dynamic dispatch **and** from reflection, so a non-writable table is
**unprobeable** (its `Insert`/`Update`/`Delete` are indistinguishable from an
unknown method) and can never become an oracle for which tables are writable.
With the global switch off, no mutation intent is ever built.

```csharp
// Schema metadata — opt a specific table into the gRPC write RPCs
"dbo.orders { grpc-write: enabled }"
```

Every write routes through the full `TableMutationPipeline` under the caller's
identity: tenant scoping, audit actor, soft-delete, field-encryption-on-write,
CDC/history hooks, validation, and optimistic concurrency all apply. The adapter
supplies only the positional primary key (all columns, composite-safe) plus the
caller's identity — it builds **no predicate of its own**. So an out-of-tenant PK
matches zero rows structurally: a scoped-away write reports the **same**
`affected_rows: 0` as a genuinely-absent row, no existence oracle. A `Delete`
routes a delete *intent* and lets the pipeline decide hard-vs-soft — the adapter
never special-cases soft-delete. Each mutation response carries `affected_rows`
(the real pipeline count) and, for `Insert`, `returned_key` (the generated
identity).

## RPC matrix

Per visible table `T` (using its `GraphQlName`), under service
`bifrostql.BifrostQuery`:

| RPC | Kind | Request | Response |
|-----|------|---------|----------|
| `GetT` | unary | `GetTRequest` (one field per key column) | `GetTResponse` (`row`) |
| `ListT` | unary | `ListTRequest` (`filter`, `order_by`, `page_size`, `page_token`) | `ListTResponse` (`rows`, `next_page_token`) |
| `StreamT` | server-streaming | `StreamTRequest` (same read fields) | stream of `TRow` |
| `InsertT` † | unary | `InsertTRequest` (all columns, optional) | `InsertTResponse` (`affected_rows`, `returned_key`) |
| `UpdateT` † | unary | `UpdateTRequest` (required keys + optional SET columns) | `UpdateTResponse` (`affected_rows`) |
| `DeleteT` † | unary | `DeleteTRequest` (required keys only) | `DeleteTResponse` (`affected_rows`) |

† Present only when `EnableWrites` is on **and** the table carries
`grpc-write: enabled`.

## Status-code matrix

Every op class funnels faults through one mapper, so the **same** underlying
condition maps to the **same** gRPC status on every RPC — a read-denied caller
cannot tell one op class from another by its status.

| Condition | gRPC status | Detail |
|-----------|-------------|--------|
| Missing / anonymous / unmapped / oversized credential | `UNAUTHENTICATED` | One fixed message for all — no issuer/user enumeration. |
| Malformed request, bad operator, over-cap filter/sort/page, bad key value | `INVALID_ARGUMENT` | Message references only the request field; validation faults also carry a `google.rpc.BadRequest` in the `grpc-status-details-bin` trailer. |
| `Get` on a missing **or** authorization-denied row | `NOT_FOUND` | Byte-identical for both — no existence oracle. |
| Authorization denial on `List`/`Stream`/writes | `PERMISSION_DENIED` | Generic message; the exception's table/tenant detail never reaches the wire. |
| Client cancel | `CANCELLED` | |
| Deadline elapsed | `DEADLINE_EXCEEDED` | |
| Any internal / `BifrostExecutionError` fault | `INTERNAL` | Generic sanitized message; full detail logged server-side only (it can wrap raw driver/schema text). |

## Security

The gRPC front door inherits every guarantee of the shared pipeline (see
[Protocol Adapters](/BifrostQL/concepts/protocol-adapters/)):

- **Fail-closed identity, before any intent.** Identity is resolved through the
  shared `IBifrostAuthContextFactory` as the first step of every RPC; an empty or
  throwing projection is rejected as `UNAUTHENTICATED`, never a degraded identity.
- **Unskippable transformers.** Reads go through `IQueryIntentExecutor` and writes
  through `IMutationIntentExecutor` — the adapter has no path to `SqlExecutionManager`,
  a `DbConnection`, or SQL text. Tenant isolation, soft-delete, policy row/column
  guards, and the full mutation chain apply by construction.
- **Reflection is authorization-filtered.** A denied table/column/RPC is absent
  from reflection and reported as `NotFound`, identical to an unknown symbol — the
  introspection surface is not a disclosure side channel.
- **Bounded resources.** Filter depth/predicates, `_in` size, sort keys, cursor
  length, page size, and stream rows are all capped before they are honored.
- **Sanitized errors.** Only the adapter's own curated request faults surface
  their message; every internal fault maps to a generic status with detail logged
  server-side only.
- **No existence oracles.** `Get` denial ≡ missing row; a scoped-away write reports
  the same `affected_rows: 0` as an absent row.

## Non-goals

The gRPC front door is deliberately bounded. Explicitly **not** provided:

- **No gRPC-Web.** Browser-facing gRPC-Web framing is not served.
- **No client-streaming or bidirectional streaming.** The only streaming RPC is
  server-streaming `Stream{Table}`; there are no client-streaming or bidi RPCs.
- **No arbitrary SQL.** Only the bounded `filter`/`order_by`/`page` surface is
  accepted; there is no raw-query RPC.
- **No cross-RPC transactions.** Each RPC is its own unit of work; there is no way
  to span multiple RPCs in one transaction.
