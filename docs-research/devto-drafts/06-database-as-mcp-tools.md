---
title: "Your database as MCP agent tools"
published: false
description: "BifrostQL presents a SQL database to Claude Code and other MCP clients as six purpose-built tools — schema map, filtered reads, aggregates, search — with the write surface off by default and every call routed through the same pipeline as GraphQL."
tags: mcp, ai, agents, database
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/mcp-server/
---

I pointed a throwaway .NET host at a two-table SQLite file, spoke JSON-RPC at its
stdin, and asked what tools it had. Here is what came back:

```
serverInfo: {"name":"BifrostQL","version":"0.4.11"}
protocolVersion: "2024-11-05"
capabilities: {"logging":{},"resources":{},"tools":{}}
```

Six tools, no configuration beyond a connection string:

| Tool | What it does |
|---|---|
| `bifrost_schema_overview` | Curated map of the whole database: tables, primary keys, FK edges both directions, behavior notes |
| `bifrost_describe_table` | Column-level detail for one table: types, nullability, keys, FKs both directions |
| `bifrost_query` | Rows from one table with a structured filter, sort, field selection, cursor pagination |
| `bifrost_row_context` | One row by primary key plus its parents and child collections, in a single call |
| `bifrost_aggregate` | GROUP BY with count/sum/avg/min/max measures |
| `bifrost_search` | Case-insensitive substring search across the string columns of every table |

All six carry `annotations: {"idempotentHint":true,"readOnlyHint":true}`. The server also
sends an instruction string that starts with "BifrostQL exposes a SQL database. Start with
bifrost_schema_overview to map the schema, then bifrost_describe_table for column-level
detail", which is how an agent knows the intended order without being told in a prompt.

## The surface is fixed on purpose

The obvious design is one tool that takes SQL or GraphQL text and runs it. That design
gives up the thing that makes this safe.

Each of these tools compiles its arguments into a *programmatic intent*, then hands it to
`IQueryIntentExecutor`. Because no tool accepts query text, there is no string for a model
to concatenate and no route to SQL that skips the transformer pipeline. Tenant isolation,
soft-delete hiding, policy row scope, and column read guards run on every call because
there is no code path where they don't.

The `filter` argument shared by `bifrost_query` and `bifrost_aggregate` is a structured
`{column: {_op: value}}` object — sibling keys AND together, `{"and":[…]}` / `{"or":[…]}`
form explicit groups. Operators are the usual set: `_eq`, `_neq`, `_lt`, `_lte`, `_gt`,
`_gte`, `_contains`, `_in`, `_between`, `_null`, plus negated and pattern variants. Every
value binds as a SQL parameter. The argument is a data structure the whole way down.

`bifrost_row_context` follows the same rule internally. Rather than a hand-rolled join, it
issues one intent per relationship, so each sub-query independently passes the pipeline.

## Setting it up

```csharp
builder.Services.AddBifrostQL(o => o
    .AddProtocolAdapter<BifrostMcpAdapter>());
```

That is the stdio host: the process speaks MCP JSON-RPC on its own stdin and stdout, which
means **nothing else in the process may write to stdout**. Send logging to stderr or a
file, or you will corrupt the protocol stream with a log line.

Register it with Claude Code by pointing at the command that starts your host:

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

A stdio session has no per-request principal — the caller is whoever launched the process
— so the default identity is an empty user context. That is a fail-closed default: a
tenant-filtered table returns nothing rather than everything. Change the posture with
`McpAuthOptions`:

```csharp
builder.Services.AddSingleton(new McpAuthOptions
{
    Mode = McpAuthMode.Bearer,
    CredentialSource = McpCredentialSources.FromEnvironment("BIFROST_MCP_TOKEN"),
    ValidateBearerToken = token => MyJwt.Validate(token),
    EnableWrites = false,
});
```

For a shared deployment there is an HTTP variant, `AddBifrostMcpHttp` plus
`MapBifrostMcp("/mcp")`, where each session's identity comes from the `Authorization:
Bearer` header on the request that initiates it. Both hosts build the identical tool
surface from the same factory; they differ in transport and in how identity arrives.

Whichever host, the adapter parses no claims itself. It hands the principal to
`IBifrostAuthContextFactory`, the same seam the GraphQL, binary, pgwire, and RESP gates
use. A token from an OIDC issuer this deployment has no mapper for fails closed at
projection, and the agent gets a generic authentication error while the issuer name stays
in the server log.

## Writes are off, and off means absent

Ask for `bifrost_insert` on a default deployment and you get this, verbatim:

```
Tool 'bifrost_insert' is a write tool and the MCP write surface is disabled.
Enable writes on the server to use it.
```

Note the tool was never in `tools/list` to begin with — the six above are the whole
default surface. The gate sits first in the dispatch path, ahead of argument parsing and
the role check, so a disabled write surface builds zero intent and cannot be probed for
behavior by malformed calls.

Flip `EnableWrites = true` and the same host lists nine tools, adding `bifrost_insert`,
`bifrost_update`, and `bifrost_delete`. An insert then succeeds and returns
`{"table":"customers","action":"insert","result":3}`. Startup also logs this to stderr:

```
MCP front door started with WRITES ENABLED — the bifrost_insert, bifrost_update,
and bifrost_delete tools are exposed…this is a deliberate opt-in, off by default.
```

There is one master gate and no per-tool toggle. When writes are on, the three tools
supply only the table, the caller's column values, and the positional primary key, then
hand off to `IMutationIntentExecutor`. The `TableMutationPipeline` does the rest: tenant
pinning, validation, encryption-on-write, soft-delete rewrite, audit and CDC hooks. The
adapter writes no WHERE predicate and never special-cases soft delete — a delete routes a
Delete intent and the pipeline decides hard versus soft.

That is what makes "agent A cannot write agent B's row" structural. Tenant scope is ANDed
onto every write, so an out-of-scope primary key matches zero rows, and a client-supplied
tenant value on an insert is overridden. No allow-list in the MCP layer is doing that work.

The `id` argument, shared by update, delete, and `bifrost_row_context`, takes a scalar, an
array in key-column order, or a `"v1|v2"` delimited string. Its own schema documentation
says "Never just the first key column", which is the composite-key trap it exists to
avoid.

## Paying attention to the agent's context budget

An agent pays tokens for everything a tool returns, so the payloads are curated rather than
dumped. Here is a real `bifrost_query` response against the fixture:

```json
{"table":"customers","detail":"summary",
 "rows":[{"id":1,"name":"Ada Lovelace"},{"id":2,"name":"Grace Hopper"}],
 "totalCount":2,"returnedCount":2,"offset":0}
```

The `customers` table also has `email` and `city`. Summary detail dropped them, keeping the
primary key and a display column. `detail: "full"` returns everything. The same dial exists
on `bifrost_schema_overview`, where summary gives keys, edges, and behavior notes, and full
inlines per-table column lists.

`bifrost_aggregate` and `bifrost_search` cap and rank instead of flooding — top groups, up
to five ranked rows per table — and attach a steering `message` when output was truncated,
so a high-cardinality result pushes the agent to narrow its query.

The same schema payloads are also MCP resources. On the fixture, `resources/list` returned
`bifrost://schema/overview`, `bifrost://schema/customers`, and `bifrost://schema/orders`.

## Failures that teach the agent something

Argument mistakes come back as `isError` tool results with actionable text, not JSON-RPC
protocol faults that tear down the session. An unknown table name returns a nearest-name
suggestion plus the valid list. A tampered pagination cursor returns one clear
invalid-cursor prompt rather than silently clamping, which would hide the tampering.

Errors raised *inside* the server get different treatment, through a single mapping funnel:

| Condition | Wire code | What the agent should do |
|---|---|---|
| Policy denial, or an identity carrying no tenant | `access_denied` | Try a permitted table, or ask the user for the missing context |
| Any other server-side execution failure | `execution_error` | Not retryable as-is; the same call fails the same way |
| Unmapped OIDC issuer | generic auth error | Re-authenticate; the issuer name never reaches the wire |

Internal exception text can name a schema-qualified table, a tenant context key, a
policy-denied column, or raw driver output. An agent needs none of that to pick its next
move, so it gets a stable code and the detail goes to the server log. Only errors the
adapter itself authors — built from the agent's own arguments or from schema it is already
allowed to see — pass through verbatim.

The same access gate is consulted by both `tools/list` and `tools/call`, so what an agent
can see and what it can invoke cannot drift apart.

## What was actually exercised

The transcripts above came from a real stdio session: `initialize`,
`notifications/initialized`, `tools/list`, `tools/call`, and `resources/list` written to the
process's stdin, against a SQLite fixture with two tables and five rows. Both postures were
run — writes off, then writes on. The repo's MCP suite also passes on this checkout:
**212 tests, 0 failed, 6 s**.

Not exercised here: the HTTP + bearer host, declarative tool documents, and any tenant or
policy denial. The fixture carried no tenant metadata, so the sanitized `access_denied`
path was confirmed by reading the mapper rather than by watching it happen on the wire.
