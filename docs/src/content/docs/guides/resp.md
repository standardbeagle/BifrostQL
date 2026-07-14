---
title: "Redis Wire Protocol (RESP)"
description: "Point redis-cli, StackExchange.Redis, or any RESP client at BifrostQL and read your tables as key-addressed rows. Covers operator setup and auth mapping, the supported command map, the <table>:<pk> key format, the opt-in write surface, unsupported-command behavior, and the security guarantees that make reads travel the same pipeline as GraphQL."
---

BifrostQL can answer the Redis serialization protocol (RESP2 and RESP3) on a TCP
port, so any tool that speaks Redis — `redis-cli`, StackExchange.Redis, any Redis
client library — can **read** your tables as key-addressed rows. It is a
[protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is Redis, but
every read still executes through the same transformer pipeline as GraphQL, so
tenant isolation, soft-delete, and policy read guards are enforced on the wire.

This is a **read-first, deliberately narrow** front door. It is not a Redis server:
it exposes a small key-space command surface mapped onto your relational rows and
answers everything outside it with a clean, honest error. Writes exist but are
**off by default** (see [Optional writes](#optional-writes)).

> Already have a running endpoint and want to validate `redis-cli` or a real
> StackExchange.Redis app end to end? Jump to the
> [RESP Smoke Runbook](/BifrostQL/guides/resp-smoke/).

## Enabling the front door

Register the adapter with `AddBifrostResp`. Registering an `IRespCredentialStore`
is a **hard requirement**, resolved fail-fast at startup, so an auth-required port
can never come up without an identity source.

```csharp
builder.Services.AddBifrostResp(o =>
{
    o.Port = 6379;                     // default 6379 (the Redis port)
    o.RequireAuthentication = true;    // default; AUTH required before any identity-bearing command
    o.Endpoint = "/graphql";           // which BifrostQL endpoint to read; null = the only one
    // o.EnableWrites = true;          // opt-in: SET/HSET/DEL through the mutation pipeline (default off)
});

// REQUIRED — the identity source AUTH authenticates against. No default registration.
builder.Services.AddSingleton<IRespCredentialStore, MyCredentialStore>();
```

`RespWireOptions`:

| Option | Default | Meaning |
|--------|---------|---------|
| `Port` | `6379` | TCP port the front door listens on. |
| `RequireAuthentication` | `true` | When true, a connection must complete `AUTH` (or inline `HELLO … AUTH`) before any identity-bearing command runs; until then those commands are refused with `NOAUTH`. There is no anonymous mode unless a deployment explicitly sets this `false`. |
| `Endpoint` | `null` | Registered BifrostQL endpoint path to read/write against; `null` selects the single registered endpoint. |
| `EnableWrites` | `false` | Master gate for the write surface (SET/HSET/DEL). Off by default: every write is refused with a clean `-ERR` and executes nothing until a deployment opts in. |
| `MaxBulkLength` | `1048576` (1 MiB) | DoS guard on the unauthenticated path: a bulk/inline length prefix beyond this is refused, never allocated. |
| `MaxAggregateElements` | `1048576` | DoS guard: a declared array/map element count beyond this is refused, so a huge multibulk count cannot pre-allocate an unbounded array. |
| `MaxNestingDepth` | `32` | DoS guard: how deeply aggregates may nest before the decoder refuses to descend (prevents a stack-overflow teardown of the host). |

> **No STARTTLS.** RESP has no in-protocol TLS. `AUTH` crosses the wire in the
> clear unless TLS is terminated at the listener or a proxy — the same operational
> contract as a real Redis. Do not send real secrets over an untrusted network
> without TLS termination.

## Authentication and identity mapping

A client authenticates with `AUTH <user> <pass>` (or `AUTH <pass>`, which resolves
the Redis-default `default` user), or inline via `HELLO <proto> AUTH <user> <pass>`.
`IRespCredentialStore.FindAsync(username)` resolves the username to a
`RespLogin(Secret, Principal)`:

- **`Secret`** — the shared secret (API key, client secret, password) the `AUTH`
  password is compared against in constant time.
- **`Principal`** — the `ClaimsPrincipal` that login maps to. This is the *candidate*
  identity only: it is still projected through
  [`IBifrostAuthContextFactory`](/BifrostQL/guides/protocol-adapters/#identity-the-auth-context-factory),
  the same fail-closed seam the HTTP GraphQL and pgwire gates use. A subject-less or
  unmapped-issuer principal is rejected there.

**Fail-closed, always.** An unknown username resolves to `null` and authentication
fails — a store must never hand back an ambient or anonymous identity to stand in
for a failed lookup. There is deliberately no default `IRespCredentialStore`
registration, so a deployment can never come up authenticating everyone to nobody.
A verified password only unlocks the *candidate* principal; the session becomes
authenticated only if that principal projects to a non-empty Bifrost user context.
The tenant/policy claims on the mapped principal are what scope every subsequent
read. There is no anonymous access unless a deployment explicitly clears
`RequireAuthentication`.

The credential lookup and password compare run in constant time (a fixed-time
compare against a decoy secret for an unknown user), so the front door does not
leak whether an account exists.

## The supported command map

### Connection plumbing

| Command | Behavior |
|---------|----------|
| `PING [msg]` | `+PONG`, or echoes `msg` as a bulk string. Requires auth. |
| `HELLO [proto [AUTH user pass] [SETNAME name]]` | Negotiates RESP `2` or `3`, optionally authenticates inline, returns the server-info reply as a RESP3 map or a RESP2 flat pair array. On an auth-required front door, HELLO without an established identity and without inline AUTH is refused with `NOAUTH` — the protocol is not switched. |
| `AUTH <pass>` / `AUTH <user> <pass>` | Authenticates the session (see above). A failed AUTH keeps the connection usable so the client can retry. |
| `SELECT <index>` | BifrostQL exposes a single logical namespace, not Redis' 16 numbered databases. Index `0` is accepted; any other index is refused honestly with `ERR DB index is out of range`. Requires auth. |
| `INFO` | A minimal, parseable `# Server` section (`redis_version`, `server_name`, `redis_mode`, `role`, client id, proto). Requires auth. |
| `ECHO <message>` | Returns the message verbatim (binary-safe — StackExchange.Redis uses ECHO as its connection tracer). Requires auth. |
| `QUIT` | `+OK`, then closes the connection. |
| `RESET` | Resets session state, `+RESET`. |
| `COMMAND …` | Minimal empty-array reply so `redis-cli`'s `COMMAND DOCS` probe on connect proceeds. Requires auth. |
| `CLIENT …` | Minimal `+OK` so `redis-cli`'s `CLIENT SETINFO`/`SETNAME` after HELLO is acknowledged. Requires auth. |

The server identifies itself honestly as `bifrostql` (not a real Redis).

### Key format

A data-command key addresses one row:

- Single-column primary key: `<table>:<pk>` — e.g. `users:42`.
- Composite primary key: `<table>:<pk1>:<pk2>[:<pk3>…]` — the primary-key values in
  **schema primary-key order**, e.g. `order_items:1001:5`.

The first segment is the table, matched case-insensitively against its database name
or its GraphQL name. The remaining segments must match the primary-key column count
exactly (a mismatch is a clean `-ERR` naming the expected columns) and each is
coerced to its key column's type (an unparseable integer/decimal segment is a clean
`-ERR`, never executed). Values bind as query parameters — a segment is never
concatenated into SQL. A key value that itself contains `:` cannot be addressed (it
splits into the wrong segment count); callers must encode such values.

### Reads

| Command | Behavior |
|---------|----------|
| `GET <key>` | The row as a JSON bulk string, or Null when the key resolves to no visible row (missing **or** filtered out by tenant/policy — indistinguishable, so a hidden row's existence never leaks). |
| `MGET <key> [key …]` | A RESP array of JSON bulk strings / Nulls positionally aligned to the requested keys. Keys of one single-PK table are batched into a single `_in` intent; each key is still tenant-filtered. |
| `EXISTS <key> [key …]` | The integer count of keys resolving to a visible row. A row the identity cannot see counts as not-existing, exactly like a missing key. |
| `TYPE <key>` | `string` for a visible row (a Bifrost row is modeled as a JSON string value), or `none` for a missing/invisible key. |

**Row → JSON shape.** A found row is a JSON object mapping each column's database
name to its value, in schema ordinal order. A SQL `NULL` is a JSON `null`. Values
serialize with System.Text.Json Web defaults: numbers/booleans as JSON scalars,
dates as ISO-8601 strings, byte arrays as base64. The JSON text is returned as a
RESP bulk string.

### Hash reads

The hash commands project a row as a Redis field/value hash. They reuse the same
single-row read path, key parser, and composite-PK mapping as GET — they differ only
in wire shape and in exposing the **visible column set** (the columns the transformer
pipeline actually returned for this identity; a policy-denied or crypto-masked column
is simply absent, never re-added and never unmasked).

| Command | Behavior |
|---------|----------|
| `HGETALL <key>` | The row's visible columns as a field/value hash. **RESP3**: a Map (`%`) of column→value. **RESP2**: a flat array (`*`) of alternating field, value bulk strings (Redis' HGETALL wire shape). A missing or tenant-hidden key returns an empty hash — indistinguishable, no leak. |
| `HGET <key> <field>` | A single visible column's value as a bulk string. Returns Null when the key resolves to no visible row, when `<field>` is not a column the pipeline returned for this identity (unknown field or existing-but-denied column — indistinguishable), or when the column's value is SQL `NULL`. |

### RESP2 vs RESP3

The front door speaks both and honors what the client negotiates with `HELLO`:

- **HGETALL** returns a native RESP3 **Map** (`%`) when the client sent `HELLO 3`,
  and a RESP2 **flat array** (`*`) of alternating field/value otherwise.
- **Null** is the RESP3 Null (`_`) on a RESP3 session and the RESP2 null bulk (`$-1`)
  otherwise.
- **HELLO** itself replies as a map (RESP3) or a flat pair array (RESP2).

### SCAN

```
SCAN <cursor> MATCH <table>:* [COUNT n] [TYPE t]
```

Cursor-paginated primary-key enumeration of one table. The reply is the Redis SCAN
2-element array `[<next-cursor>, [<table>:<pk…> keys]]`; iteration begins at cursor
`0` and ends when the returned cursor is `0`.

- **MATCH is required**, and only `<table>:*` is supported. A partial glob
  (`users:1*`), a missing MATCH, or an unknown table is a clean `-ERR` — never a
  silent over-broad enumeration.
- **COUNT** is a page-size hint, defaulting to `10` and capped at `1000`. A
  non-positive or unparseable COUNT is a syntax error.
- **TYPE** is accepted and ignored — a Bifrost row has no single Redis type, so
  filtering by it would be dishonest.
- The **cursor** is opaque and encodes only a primary-key position; a malformed
  cursor is a clean `-ERR` and executes nothing.

Enumeration runs through the query pipeline under the session identity, so only
primary keys of rows the identity may see are ever emitted — on every page.

## Optional writes

The write surface (`SET`/`HSET`/`DEL`) is **off by default**. Until a deployment sets
`RespWireOptions.EnableWrites = true`, every write command is refused with
`ERR write commands are disabled` and executes nothing — no intent is ever built, and
the disabled surface cannot be probed for schema shape. This is the highest-risk
surface, so it stays dark unless deliberately turned on; enabling it is logged at
startup as a posture change.

When enabled, writes route **only** through `IMutationIntentExecutor` — the full
mutation transformer chain (tenant scoping, the audit actor, soft-delete,
field-encryption-on-write, CDC/history hooks) applies and cannot be skipped. The
adapter supplies only the positional primary key from the key plus the session
identity; it builds no `WHERE` predicate of its own, so the pipeline narrows scope
from the identity and a write targeting a row outside the caller's tenant/policy
scope affects **zero rows**.

| Command | Behavior |
|---------|----------|
| `SET <key> <json>` | **Update-only.** Sets the addressed row's columns from a JSON object (the same column-name → value shape GET returns). It does **not** insert a missing row — an addressed-but-absent or out-of-scope key is narrowed out by the pipeline and the write is a no-op. The primary key comes from the key, not the body; a PK column in the body must equal the key value (a conflict is a clean `-ERR`) and is never a SET column. Requires at least one non-PK column. Reply is always `+OK` (a no-op update still replies OK, exactly as Redis SET does not report prior existence). |
| `HSET <key> <field> <value> [field value …]` | Updates the named columns of the addressed row. Each field is validated against the table's columns (unknown → clean `-ERR`); a primary-key column cannot be set via HSET (the PK comes from the key). Reply is the integer count of fields written (the number of field/value pairs supplied) — distinct from Redis' "new fields" count, since a row's columns pre-exist. |
| `DEL <key> [key …]` | Deletes the addressed rows, one delete intent per key. The **pipeline** decides hard vs soft delete (a table with soft-delete metadata is soft-deleted by its transformer — the adapter never special-cases it). Every key is validated up front, so one malformed key rejects the whole command with no partial delete. Reply is the integer count of keys that actually deleted a row (a key whose row is missing or out of scope is a no-op and is not counted). |

## Unsupported commands and non-goals

Any command the front door does not implement gets the Redis-compatible unknown-command
error, naming the command and echoing its arguments so a client gets the same
actionable guidance a real Redis sends:

```
ERR unknown command 'LPUSH', with args beginning with: 'x', 'y'
```

The command name is quoted verbatim and never dispatched, so an unsupported command
can neither hang the connection loop nor leak internals — the connection stays usable
and you can issue a supported command on the same session.

**Explicit non-goals** (each returns the clean unknown-command `-ERR`, never a partial
or faked result): TTL / expiry (`EXPIRE`, `TTL`, `PERSIST`), pub/sub
(`SUBSCRIBE`, `PUBLISH`), Lua scripting (`EVAL`), cluster / sentinel commands,
transactions (`MULTI`/`EXEC`), and the Redis native collection operations
(list `LPUSH`/`LRANGE`, set, sorted-set, stream commands). BifrostQL exposes your
relational rows as key-addressed values, not native Redis data structures.

An unexpected server-side execution fault (e.g. a policy read-deny deeper in the
pipeline) is answered with a sanitized `ERR internal error`; the real exception is
logged server-side and never crosses the wire.

## Security guarantees

Everything above rolls up to a short list a security reviewer can check:

- **Fail-closed identity.** No credential store means the port does not start; an
  unknown user or unmapped issuer is rejected, never defaulted to an ambient identity.
  No anonymous access unless `RequireAuthentication` is explicitly cleared.
- **Same pipeline as GraphQL.** Reads execute through `IQueryIntentExecutor`, so every
  registered filter transformer and column read guard applies unconditionally — tenant
  filter, soft-delete, and policy read guards hold on the wire. A row the identity
  cannot see is indistinguishable from a missing key, so existence never leaks.
- **Writes fail-closed and pipeline-bound.** Off by default; when enabled they route
  only through `IMutationIntentExecutor`, and the adapter never builds its own
  predicate — out-of-scope writes affect zero rows structurally.
- **Parameterized always.** Table names are validated against the schema and key/column
  values bind as parameters through the intent executors, never concatenated into SQL.
- **Sanitized errors.** An out-of-subset command is a clean `-ERR` and the connection
  survives; an internal fault is a generic `ERR internal error`, no DB internals on the
  wire.
- **Bounded decoder.** The unauthenticated codec caps bulk length, aggregate element
  count, and nesting depth, so a hostile frame cannot exhaust memory or crash the host.

## Connecting clients

### redis-cli

```bash
redis-cli -h localhost -p 6379 --user <login> --pass <secret>
```

```
PING                                 -- +PONG (connection is live)
GET <table>:<pk>                     -- the row as a JSON string
HGETALL <table>:<pk>                 -- the row's visible columns as a field/value hash
SCAN 0 MATCH <table>:* COUNT 100     -- enumerate ONLY your tenant's keys
LPUSH x y                            -- clean ERR unknown command …, connection stays live
```

### StackExchange.Redis

```csharp
var config = new ConfigurationOptions
{
    EndPoints = { { "localhost", 6379 } },
    User = "<login>",
    Password = "<secret>",
    AbortOnConnectFail = false,
};
await using var mux = await ConnectionMultiplexer.ConnectAsync(config);
var db = mux.GetDatabase();

Console.WriteLine(db.Ping());                                // round-trips
foreach (var e in await db.HashGetAllAsync("<table>:<pk>"))  // your tenant's row
    Console.WriteLine($"{e.Name}={e.Value}");
var scan = (RedisResult[])await db.ExecuteAsync("SCAN", "0", "MATCH", "<table>:*", "COUNT", "100");
```

The full step-by-step validation — including confirming tenant isolation with a second
login — is the [RESP Smoke Runbook](/BifrostQL/guides/resp-smoke/).

## See also

- [Protocol Adapters (concept)](/BifrostQL/concepts/protocol-adapters/) — why an adapter owns only its wire and codec.
- [Authoring a Protocol Adapter](/BifrostQL/guides/protocol-adapters/) — the intent APIs and conformance kit RESP is built on.
- [PostgreSQL Wire Protocol (pgwire)](/BifrostQL/guides/pgwire/) — the sibling read-only SQL front door.
- [RESP Smoke Runbook](/BifrostQL/guides/resp-smoke/) — validate redis-cli / StackExchange.Redis end to end.
- [Authentication](/BifrostQL/guides/authentication/) — how principals and claims are mapped.
</content>
</invoke>
