---
title: Declarative MCP Tool Document
description: The full DSL for authoring domain-specific MCP tools over a BifrostQL database — params, root/byId reads, includes, aggregates, detail gating, per-tool policy, and off-by-default mutation tools — every key matched to the loader and validator, with the narrow-never-widen security guarantee.
---

The declarative MCP tool document lets you ship **domain-specific** agent tools
(`get_customer_context`, `create_support_ticket`) on top of the generic MCP
surface described in the [MCP Server guide](/guides/mcp-server/). You declare
each tool as data — a table, a key parameter, the related data to fold in — and
BifrostQL **compiles** it against the live schema. Compiled reads run through the
same per-request transformer pipeline as every other query, so a declared tool
can **narrow** what an agent sees but never **widen** it, and there is no
arbitrary-SQL escape hatch.

Register a document with [`AddBifrostMcpTools`](#registration). It is loaded once
at startup and **validated against the live model** — a reference to a missing
table, column, or relation fails the host start, not the first tool call.

## A third configuration document

The tool document is a **third, separate** configuration surface. It is never
merged with either of the other two:

| Document | Controls | Audience |
| --- | --- | --- |
| **Schema metadata** (`tenant-filter`, `soft-delete`, …) | Server-enforced security & semantics | The query pipeline |
| **[App-metadata overlay](/concepts/app-metadata-overlay/)** | Client presentation (labels, forms, grids) | SPA / React Native clients |
| **Declarative tool document** (this page) | Which agent tools exist and what they read/write | LLM agents over MCP |

Keep them distinct: schema metadata decides what a tool is *allowed* to return;
the tool document only decides which consolidated tools *exist*.

## Document shape

```json
{
  "version": 1,
  "tools": [ /* one object per tool */ ]
}
```

| Key | Required | Notes |
| --- | --- | --- |
| `version` | yes | Must be `1`. Any other value is rejected at load. |
| `tools` | yes | Array of tool definitions. Names must be unique and must not collide with a built-in tool (`bifrost_query`, `bifrost_row_context`, `bifrost_aggregate`, `bifrost_search`, `bifrost_insert`, `bifrost_update`, `bifrost_delete`, `bifrost_schema_overview`, `bifrost_describe_table`). |

## Tool definition

A tool declares **either** a read `root` **or** a write `mutation` — never both.

| Key | Applies to | Notes |
| --- | --- | --- |
| `name` | all | Unique tool name (the MCP tool id). |
| `description` | all | Non-empty, at least 10 characters. This is what the agent reads to choose the tool — write it for the agent. |
| `params` | all | Map of parameter name → parameter definition. |
| `root` | read tools | The row the tool fetches. See [Root](#root-read-tools). |
| `include` | read tools | Related data to fold into the response. See [Includes](#includes-read-tools). |
| `policy` | all | Per-tool presentation/role flags. See [Policy](#policy). |
| `mutation` | write tools | The single write the tool performs. See [Mutation tools](#mutation-tools-write). |

### Params

```json
"params": {
  "customerId": { "type": "id", "table": ".Customers", "description": "Customer primary key." },
  "detail":     { "type": "enum", "values": ["summary", "full"], "default": "summary", "description": "…" }
}
```

| Key | Notes |
| --- | --- |
| `type` | `id`, `string`, `int` (alias `integer`), `number`, `bool` (alias `boolean`), or `enum`. |
| `table` | Only for `type: "id"` — the schema-qualified table the key belongs to; validated against the model. |
| `description` | Strongly recommended. A parameter with no description logs a load-time warning. |
| `values` | Only for `type: "enum"` — the allowed values. |
| `default` | When present the parameter is **optional** (and omitted from the tool's `required` list). For an `enum`, the default must be one of `values`. |

A parameter named `detail` is **reserved** for [detail gating](#detail-gating): if
any include declares `detailGate`, a declared `detail` parameter must be
`enum ["summary", "full"]`.

### Root (read tools)

```json
"root": {
  "table": ".Customers",
  "byId": "customerId",
  "fields": ["Id", "Name", "Email", "City"]
}
```

| Key | Notes |
| --- | --- |
| `table` | Schema-qualified table name, e.g. `dbo.Customers` (or `.Customers` when the schema is empty). Validated against the model. |
| `byId` | The parameter (which must have `type: "id"`) carrying the primary key. Composite keys are supported — pass an array in key order or a `v1\|v2` string. |
| `fields` | The columns to return. Each is validated against the table. |

The root is fetched by primary key through `IQueryIntentExecutor`, so tenant
scoping, soft-delete hiding, and column policies apply. An out-of-scope key
returns no row.

### Includes (read tools)

Each `include` folds one related record set (or aggregate) into the response,
keyed by `as`.

```json
"include": [
  { "relation": "orders", "as": "recentOrders",
    "fields": ["Id", "OrderDate", "Total", "Status"], "sort": "-Id", "limit": 5 },
  { "relation": "orders", "as": "orderStats", "aggregate": { "count": true, "sum": "Total" } }
]
```

| Key | Notes |
| --- | --- |
| `relation` | The **model relationship name** on the root table (single-, multi-, or many-to-many link). Validated against the model. |
| `as` | The output key for this include. Must be unique within the tool. |
| `fields` | Columns of the related table to return (a collection include). Composite foreign keys are matched on every column pair — never a single-column guess. |
| `filter` | A structured filter `{ "column": { "_op": value } }` ANDed onto the relation. Supports `and`/`or` groups. Column names are validated. All values bind as SQL parameters. |
| `sort` | A single column, ascending; prefix `-` for descending (`"-Id"`). |
| `limit` | Maximum related rows to return. |
| `aggregate` | Aggregate measures over the relation. See below. |
| `detailGate` | `"full"` hides this include unless the call passes `detail: "full"`. See [detail gating](#detail-gating). |

#### Aggregate

```json
"aggregate": { "count": true, "sum": "Total", "avg": "Total", "min": "Total", "max": "Total" }
```

| Key | Notes |
| --- | --- |
| `count` | `true` to emit a correlated row count. |
| `sum` / `avg` / `min` / `max` | The related column to aggregate; validated against the related table. |

Each measure declares an `<as>_<measure>` field (e.g. `orderStats_count`) on the
tool's output schema. Declared aggregate filters compose with the relation
predicate in SQL, and every value binds as a parameter.

> **Note:** aggregate-measure values are declared on the output schema but are
> not yet populated at runtime — the current execution path surfaces only
> `fields`-based includes. Use a `fields` include when you need the value in the
> response today. Tracked for a follow-up.

#### Detail gating

An include with `"detailGate": "full"` is omitted from the default (`summary`)
response and included only when the caller passes `detail: "full"`. This keeps
the token-dense default lean while still allowing an agent to ask for more. When
any include is detail-gated, either declare a `detail` enum parameter
(`["summary", "full"]`) or let the tool surface add one automatically.

### Policy

```json
"policy": { "hiddenFieldBehavior": "omit", "allowedRoles": ["support"] }
```

| Key | Notes |
| --- | --- |
| `hiddenFieldBehavior` | How a policy-hidden field is represented (default `omit`). |
| `allowedRoles` | Role names permitted to see and call the tool. Role gating is fail-closed and shared with the same identity projection the data path uses. |

## Mutation tools (write)

A tool with a `mutation` block is a **write** tool. The entire declared-write
surface is **off by default**; it is listed and callable only when the server
opts in (the same `EnableWrites` flag the built-in write tools use), and enabling
it logs a startup warning.

```json
{
  "name": "create_support_ticket",
  "description": "File a support ticket for the caller's tenant.",
  "params": {
    "subject": { "type": "string", "description": "Ticket subject." },
    "body":    { "type": "string", "description": "Ticket body." }
  },
  "mutation": {
    "table": ".Tickets",
    "action": "insert",
    "values": { "subject": "$subject", "body": "$body", "status": "open" }
  }
}
```

| Key | Notes |
| --- | --- |
| `table` | Schema-qualified target table; validated against the model. |
| `action` | `insert`, `update`, or `delete`. |
| `values` | Column → value map for `insert`/`update`. A string `"$param"` binds the named parameter's call-time value; any other JSON value is a **fixed literal**. Insert and update require at least one value; delete must not declare `values`. |
| `byId` | For `update`/`delete`: the parameter (`type: "id"`) carrying the positional primary key. Insert must not declare `byId`. |

**How a declared write stays safe:**

- It executes **only** through `IMutationIntentExecutor` — the full mutation
  pipeline (tenant scoping, audit actor, soft-delete, field encryption, history
  hooks). The tool renders no SQL and builds **no `WHERE`/predicate**: it supplies
  only column values, the positional primary key, and the caller's identity, so
  an **out-of-scope key affects zero rows**.
- A **fixed literal for a security-pinned column cannot widen scope.** If a
  document hard-codes `tenant_id`, the tenant transformer still pins the caller's
  tenant — the literal never overrides it.
- **Destructive actions (`update`/`delete`) require explicit confirmation.** They
  carry a `destructiveHint`; an unconfirmed call builds no intent. Pass
  `"confirm": true` to proceed.
- The enable gate is checked **before** any argument parsing or intent
  construction, so a disabled server never builds a write intent even when a tool
  is invoked by name.

## Security guarantee

- **Compiled reads run through `QueryTransformerService` per request.** A declared
  read tool cannot see more than the caller's identity allows — tenant filters,
  soft-delete hiding, and column policies apply on every call, not at compile time.
- **Narrow, never widen.** A tool's `filter`, `byId`, and `fields` can only
  restrict the result. There is no arbitrary-SQL or expression escape hatch — the
  only predicate an author can express is the structured filter, whose columns and
  operators are validated and whose values always bind as parameters.
- **Writes are off by default and confirmed.** See [Mutation tools](#mutation-tools-write).

## Tool budget

Declared tools count against the same [tool budget](/guides/mcp-server/) as the
built-ins. The defaults warn past **12** tools and hard-fail load past **24**
(`McpToolBudgetOptions.WarnThreshold` / `.HardCap`). The budget nudges you toward
**consolidation over proliferation** — see the
[authoring guide](/guides/mcp-tool-authoring/).

## Registration

```csharp
builder.Services.AddBifrostMcpTools("mcp-tools.json");        // from a file path
// or from a stream / custom IDeclarativeToolDocumentSource
```

`AddBifrostMcpTools` loads and shape-checks the document immediately, then
registers a hosted service that validates every tool against the live model at
startup. A bad reference (unknown table, column, relation, or parameter) fails
the host start with a precise message — it never ships a tool that would only
fault on first use.
