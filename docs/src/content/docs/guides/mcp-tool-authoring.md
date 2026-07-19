---
title: Authoring MCP Tools
description: How to design declarative MCP tools for a BifrostQL database — consolidate related lookups into one rich tool instead of many thin ones, stay under the tool budget, fold related data with includes, and add off-by-default write tools with confirmation.
---

BifrostQL ships a small, fixed set of generic MCP tools (schema, query,
aggregate, search — see the [MCP Server guide](/guides/mcp-server/)). On top of
those you can author **domain-specific** tools with the
[declarative tool document](/reference/mcp-declarative-tools/). This guide is
about *how to design them well*.

## Consolidate, don't proliferate

The single most important rule: **prefer one rich tool over many thin ones.**

An agent picks tools from their names and descriptions. Five near-identical
customer getters (`get_customer`, `get_customer_orders`, `get_customer_address`,
`get_customer_tickets`, `get_customer_invoices`) force the agent to fan out
several calls, burn context re-reading envelopes, and guess which tool to start
with. One consolidated tool answers the real question in a single call:

```json
{
  "name": "get_customer_context",
  "description": "A customer with their recent orders and lifetime order stats — the one call to understand a customer.",
  "params": { "customerId": { "type": "id", "table": ".Customers", "description": "Customer primary key." } },
  "root": { "table": ".Customers", "byId": "customerId", "fields": ["Id", "Name", "Email", "City"] },
  "include": [
    { "relation": "orders", "as": "recentOrders",
      "fields": ["Id", "OrderDate", "Total", "Status"], "sort": "-Id", "limit": 5 },
    { "relation": "orders", "as": "orderStats", "aggregate": { "count": true, "sum": "Total" } }
  ]
}
```

This is **measurably** cheaper. BifrostQL's own eval harness runs the same
"customer's open orders and their total" task both ways: the generic tools take
two calls (`bifrost_query` then `bifrost_aggregate`), while a single consolidated
declared tool takes **one call and fewer response bytes**. Fewer calls, fewer
tokens, fewer chances for the agent to go wrong.

### Signs you should consolidate

- Two tools that are almost always called together.
- A tool whose name is `<entity>_<one_field>` — fold the field into the entity tool.
- An agent transcript that repeatedly calls tool A, then immediately tool B with A's output.

## Mind the tool budget

Declared tools share a budget with the built-ins. By default BifrostQL **warns**
once the surface passes **12** tools and **fails to load** past **24**
(`McpToolBudgetOptions.WarnThreshold` / `.HardCap`). Treat the warning as a design
signal: a growing tool count usually means thin tools that should be consolidated,
not a cap to raise. Raise `HardCap` only deliberately, when the tools are
genuinely distinct.

## Fold related data with includes

Each `include` folds one relationship into the response. Use them to answer the
whole question in one call:

- **`fields` + `limit` + `sort`** for a bounded list of related rows
  (`"recentOrders"`, newest five).
- **`aggregate`** for the counts and totals an agent would otherwise compute by
  paging every row.
- **`filter`** to scope the relation (only `open` orders).
- **`detailGate: "full"`** for expensive or rarely-needed relations — they stay
  out of the default `summary` response and appear only when the caller asks for
  `detail: "full"`. Keep the common path token-dense; let the agent opt into more.

Composite primary keys and composite foreign keys are handled for you — the
compiler matches every key column, so you never index the first column of a
multi-column key.

## Write tools: off by default, and confirmed

Declared **mutation** tools map parameters onto a single write through the full
mutation pipeline. Keep three things in mind:

1. **They are off by default.** Nothing is listed or callable until the server
   opts into writes; enabling logs a startup warning. Author them freely — a
   read-only deployment simply never exposes them.
2. **You supply values, not a `WHERE`.** For `update`/`delete` you give a `byId`
   parameter (the positional primary key); the pipeline ANDs the caller's scope
   on. An out-of-tenant key writes zero rows. Never try to express your own
   predicate — there is no way to, by design.
3. **Destructive actions require confirmation.** `update` and `delete` carry a
   `destructiveHint` and build no intent unless the call passes `"confirm": true`.

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

A fixed literal like `"status": "open"` is a sensible default. A fixed literal for
a **security** column (a `tenant_id`) is harmless — the pipeline's transformer
still pins the caller's tenant, so the literal can never widen scope.

## Write for the agent

- **Descriptions are prompts.** Say what the tool answers and when to use it, not
  just what table it hits. This is the text the model reasons over.
- **Describe every parameter.** An undescribed parameter logs a warning and leaves
  the agent guessing.
- **Name tools by intent** (`get_customer_context`), not by mechanism
  (`select_customers_join_orders`).

## Ship it

Register the document and let startup validation catch mistakes early:

```csharp
builder.Services.AddBifrostMcpTools("mcp-tools.json");
```

Every table, column, relation, and parameter reference is checked against the
live model at host start — a typo fails the boot, not a production tool call. See
the [DSL reference](/reference/mcp-declarative-tools/) for every key.
