---
title: "MCP Server: Your Database as Agent Tools"
description: "Expose a BifrostQL database to an LLM agent over the Model Context Protocol. Covers stdio setup for Claude Code and local agents, HTTP + bearer hosting for shared deployments, the auth and write opt-in flags, the fixed tool surface (schema, query, aggregate, search, and the opt-in write tools), and the tool-design rationale — chunky fixed surface, densified payloads, and errors-as-prompts — plus the security posture every tool inherits from the shared intent pipeline."
---

BifrostQL can present your database to an LLM agent as a set of
[Model Context Protocol](https://modelcontextprotocol.io) tools. Point Claude
Code, or any MCP client, at a BifrostQL MCP server and the agent can map your
schema, read rows with structured filters and cursor pagination, aggregate,
search across tables, and — when a deployment opts in — insert, update, and
delete rows.

Like every non-GraphQL front door, the MCP server is a
[protocol adapter](/BifrostQL/concepts/protocol-adapters/): it owns only the
wire (MCP/JSON-RPC) and the tool codec. Every read executes through
`IQueryIntentExecutor` and every write through `IMutationIntentExecutor`, so
tenant isolation, soft-delete hiding, policy column guards, and the full
mutation pipeline are enforced on the wire exactly as they are for GraphQL —
the adapter has no API that could bypass them.

There are two ways to host it:

- **stdio** — the host process speaks MCP on its stdin/stdout. This is the
  local / single-caller mode: Claude Code launches the process, and the caller
  is whoever launched it. Registered with
  `AddProtocolAdapter<BifrostMcpAdapter>()`.
- **Streamable HTTP + bearer** — the MCP server is mounted on an HTTP route and
  each session's identity comes from the `Authorization: Bearer` header of the
  request that initiates it. This is the hosted / multi-caller mode. Registered
  with `AddBifrostMcpHttp` + `MapBifrostMcp`.

Both hosts build the identical tool surface from
`BifrostMcpServerFactory.CreateServerOptions`; they differ only in transport and
in how a session establishes identity.

## Setup: stdio (Claude Code / local)

Register the adapter alongside your BifrostQL endpoint. A stdio MCP session has
no per-request principal — the caller is whoever launched the process — so the
default identity is an **empty user context** (fail closed):

```csharp
builder.Services.AddBifrostQL(o => o
    .AddProtocolAdapter<BifrostMcpAdapter>());
```

Because the host process now speaks MCP JSON-RPC on stdout, **nothing else in
the process may write to stdout**. Configure logging to stderr or a file.

To register it as an MCP server for Claude Code, point the client at the command
that starts your host — for example in `.mcp.json`:

```json
{
  "mcpServers": {
    "bifrost": {
      "command": "dotnet",
      "args": ["run", "--project", "src/YourApp.Host"]
    }
  }
}
```

The auth posture and the write opt-in are carried by `McpAuthOptions`, an
optional dependency the adapter resolves from DI (absent, it defaults to
fail-closed with writes off). Register one to change the posture:

```csharp
builder.Services.AddSingleton(new McpAuthOptions
{
    Mode = McpAuthMode.Bearer,                       // validate a token before minting identity
    CredentialSource = McpCredentialSources           // read the token from a process env var
        .FromEnvironment("BIFROST_MCP_TOKEN"),
    ValidateBearerToken = token => MyJwt.Validate(token), // your handler → ClaimsPrincipal or null
    EnableWrites = false,                             // keep the write surface off (default)
});
```

## Setup: HTTP + bearer (hosted)

For a shared deployment, mount the MCP server on an HTTP route. Each session's
identity is resolved from the bearer token on the request that initiates the
session, projected through the same shared identity factory:

```csharp
var mcpAuth = new McpAuthOptions
{
    Mode = McpAuthMode.Bearer,
    ValidateBearerToken = token => MyJwt.Validate(token),
    // EnableWrites = true,   // opt-in; off by default
};

builder.Services.AddBifrostMcpHttp(mcpAuth, endpoint: "/graphql");

// ... later, on the app:
app.MapBifrostMcp("/mcp");   // default route is /mcp
```

On the initialize request the bearer is extracted from the `Authorization`
header, the (async) credential exchange / validation is awaited on the ASP.NET
request path, and the resolved principal is projected once through the shared
factory and snapshotted for the session. An absent or invalid token mints **no**
identity, so tenant-filtered reads fail closed exactly like the stdio path.

### Config flags (`McpAuthOptions`)

| Flag | Default | Meaning |
|------|---------|---------|
| `Mode` | `FailClosed` | `FailClosed`: no identity source; empty context; tenant reads fail closed; silent. `AnonymousDev`: same runtime behavior but logs a deliberate-opt-in startup warning. `Bearer`: validate the presented token **before** minting identity; a valid token's principal is handed to the factory. |
| `EnableWrites` | `false` | Master gate for the write tools (`bifrost_insert`, `bifrost_update`, `bifrost_delete`). Off by default: the write tools are never listed and build zero intent. Enabling it logs a startup warning. |
| `BearerToken` | `null` | A single static token for the session (used only in `Bearer` mode when `CredentialSource` is unset). Null/empty presents no credential → fail closed. |
| `CredentialSource` | `null` | Per-transport delegate that reads *where* the raw credential lives — build one with `McpCredentialSources.FromEnvironment(...)` (stdio) or `.FromAuthorizationHeader(...)` (HTTP). Returns null → fail closed. |
| `ValidateBearerToken` | `null` | Host-supplied JWT handler: a token → `ClaimsPrincipal` (valid) or `null` (invalid). The adapter reads no claims itself; it hands the whole principal to the factory. |
| `CredentialStore` | `null` | Optional OIDC / token-exchange store (`IMcpCredentialStore`). When set, the extracted upstream token is exchanged for a candidate principal instead of using `ValidateBearerToken`. A failed/unknown exchange resolves to `null` — never an ambient identity. Off unless configured. |

## Security posture (as implemented)

Every tool inherits the protocol-adapter security guarantee — nothing is
re-implemented on the MCP side:

- **Reads go through `IQueryIntentExecutor`.** `bifrost_query`,
  `bifrost_row_context`, `bifrost_aggregate`, and `bifrost_search` compile to
  programmatic intents; the transformer pipeline (tenant isolation, soft-delete
  hiding, policy row scope, column read guards) applies unconditionally. No code
  path here renders SQL or GraphQL text from model input.
- **Writes go through `IMutationIntentExecutor` only.** The write tools supply
  only the table, the caller's column values, and the positional primary key.
  The full `TableMutationPipeline` (tenant pinning, soft-delete rewrite,
  validation, field-encryption-on-write, audit, CDC/history hooks) decides every
  security-relevant outcome. The adapter builds no WHERE predicate and
  never special-cases soft-delete — a delete routes a Delete intent and the
  pipeline decides hard-vs-soft.
- **The mutation surface defaults OFF.** The three write tools are exposed only
  when a deployment sets `EnableWrites = true` *and* an `IMutationIntentExecutor`
  is available. When disabled they are never listed, so a disabled surface builds
  zero intent and cannot even be probed for behavior. There is no per-tool
  toggle: `EnableWrites` is the single master gate.

  > **Per-row scope is the pipeline's, not an allow-list.** The MCP layer holds
  > no per-table allow-list. Write authorization and row scoping are the
  > pipeline's job: tenant scope is ANDed onto every write, so a row outside your
  > scope matches nothing and affects zero rows, and any client-supplied tenant
  > value on an insert is overridden. "Caller A cannot write caller B's row"
  > holds structurally, not because the adapter remembered to filter.

- **Identity is projected through `IBifrostAuthContextFactory`** — the same
  fail-closed seam the GraphQL, binary, pgwire, and RESP gates use. The adapter
  parses no claims of its own. A token from an OIDC issuer this deployment has no
  claim mapper for **fails closed on projection**; the MCP layer catches that and
  returns a *sanitized* tool error ("Authentication failed: the presented token
  could not be resolved to an identity."), logging the specific issuer
  server-side only — never a degraded or anonymous context.

## The tool surface

The server exposes a fixed set of tools. The four read tools plus the two schema
tools are always present; the three write tools appear only when writes are
enabled. Every argument name below matches the tool's input schema verbatim.

### Schema tools

| Tool | Arguments | Returns |
|------|-----------|---------|
| `bifrost_schema_overview` | `detail` (`"summary"` \| `"full"`, default `"summary"`) | Curated map of the whole database: every table with primary key, foreign-key edges (both directions), and behavior notes. `detail=full` inlines condensed per-table column lists. Row counts and sample values are never included. |
| `bifrost_describe_table` | `table` (required) | Column-level detail for one table: columns with types and nullability, primary key, foreign keys in both directions, and behavior notes. An unknown table name returns a prompt-style error with a nearest-name suggestion and the table list. |

### Read tools

| Tool | Arguments | Returns |
|------|-----------|---------|
| `bifrost_query` | `table`, `filter`, `fields`, `sort`, `page` (`{ limit, cursor }`), `detail` (`"summary"` \| `"full"`) | Rows from one table with a structured filter, sort, field selection, and opaque-cursor pagination (default 25 rows/page; follow `nextCursor`). `table` is required unless `page.cursor` is set. |
| `bifrost_row_context` | `table`, `id` | One row by primary key, plus each FK parent resolved to its key and display name, and each child collection summarized as a total count and its first rows. |
| `bifrost_aggregate` | `table`, `groupBy`, `measures` (`[{ fn, column }]`, `fn` ∈ count/sum/avg/min/max), `filter` | GROUP BY aggregation over one table. `measures` is required; `column` is required for sum/avg/min/max and omitted for count. The filter is applied before grouping. |
| `bifrost_search` | `term` (min 2 chars), `tables` | Case-insensitive substring search across the string columns of every table (or the supplied `tables`). Returns up to 5 ranked rows per table — id (usable as a `bifrost_row_context` id), display name, matched columns — plus per-table match totals. |

The `filter` argument (shared by `bifrost_query` and `bifrost_aggregate`) is a
structured `{column: {_op: value}}` object; sibling keys AND together, and
`{"and":[...]}` / `{"or":[...]}` form explicit groups. Operators: `_eq`, `_neq`,
`_lt`, `_lte`, `_gt`, `_gte`, `_contains`, `_in`, `_between`, `_null` (plus the
negated/pattern variants `_ncontains`, `_starts_with`, `_ends_with`, `_like`,
`_nin`, `_nbetween`). Values always bind as SQL parameters — the argument is a
data structure, never a SQL fragment.

### Write tools (opt-in)

Present only when `EnableWrites = true`:

| Tool | Arguments | Returns |
|------|-----------|---------|
| `bifrost_insert` | `table`, `values` (object of column values) | Inserts one row through the mutation pipeline (tenant id pinned to your identity; validation, encryption-on-write, and audit hooks apply). Returns the generated identity. |
| `bifrost_update` | `table`, `id`, `set` (object of columns to change) | Updates one row by primary key. Your tenant scope is ANDed on, so an out-of-scope row affects zero rows. Returns the number of rows affected. |
| `bifrost_delete` | `table`, `id` | Deletes one row by primary key. On a soft-delete table the pipeline marks it deleted rather than removing it. Returns the number of rows affected. |

The `id` argument (shared by update, delete, and `bifrost_row_context`) is a
primary-key value: a scalar, an array in key-column order, or a `"v1|v2"`
delimited string for composite keys — never just the first key column. Arity and
column coercion are enforced downstream by the pipeline (composite-key safe).

### Schema resources

The same schema payloads are also served as MCP resources:

- `bifrost://schema/overview` — the full-detail schema map.
- `bifrost://schema/{table}` — one table's description (URL-escaped table name).

## Tool-design rationale

The tool surface is shaped by three deliberate decisions, made for the whole
surface rather than tool by tool.

### 1. A chunky, fixed surface — not a generic query hole

Rather than exposing one "run this GraphQL/SQL" tool, the server ships a small
set of purpose-built tools, each of which compiles caller arguments to a
*programmatic intent*. This is what lets the transformer pipeline be
unskippable: because no tool accepts query text, there is nothing for a caller
to concatenate and no path that reaches SQL without the tenant, soft-delete, and
policy transformers having run. The fixed surface is also the security boundary —
`bifrost_row_context`, for instance, is implemented as one intent per
relationship rather than a hand-rolled join, so each sub-query independently
passes the pipeline, a documented simplicity choice over re-deriving join SQL for
a fixed access pattern.

### 2. Densified payloads — earn the agent's context back

An agent pays for every token it reads, so the tools return dense, curated views
instead of raw dumps. `bifrost_schema_overview` is a single call that maps the
whole database — keys, relationship edges, behavior notes — with a
`detail=summary`/`full` dial so the agent inlines per-table columns only when it
needs them. `bifrost_query`'s `summary` detail returns the primary key, a display
column, and short text columns rather than every column. `bifrost_row_context`
bundles a row plus its entire parent/child neighborhood into one response.
`bifrost_aggregate` and `bifrost_search` cap and rank their output (top groups,
top matches per table) with a steering `message` when results were truncated —
so a high-cardinality result steers the agent to narrow, instead of flooding its
context.

### 3. Errors-as-prompts — every failure is actionable, none is a protocol fault

Argument mistakes and execution-layer rejections (a missing tenant context, a
policy-denied column, an unsupported filter shape, an out-of-range cursor) surface
as **prompt-style tool errors** — the tool returns `isError` with a message the
agent can act on, not a JSON-RPC protocol fault that tears down the session. An
unknown table or column name comes back with a nearest-name "did you mean"
suggestion and the list of valid names. A tampered or corrupted pagination cursor
collapses to one clear invalid-cursor prompt rather than silently clamping (which
would mask tampering). The one thing that is *not* forwarded verbatim is an
identity failure from an unmapped OIDC issuer: its message names the issuer, so it
is sanitized to a generic authentication error and the detail is logged
server-side only. The result is a surface an agent can drive by trial and
correction, where every recoverable failure teaches it what to send next.

## See also

- [Protocol Adapters: One Pipeline, Many Front Doors](/BifrostQL/concepts/protocol-adapters/) — why every front door inherits these guarantees.
- [Authoring a Protocol Adapter](/BifrostQL/guides/protocol-adapters/) — the seam these tools are built on.
- [Authentication](/BifrostQL/guides/authentication/) — how identity and claim mapping work across transports.
