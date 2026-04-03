---
title: "Solving the GraphQL N+1 Query Problem"
description: "How BifrostQL eliminates the N+1 query problem by compiling GraphQL requests into batched SQL — one query per table, one database round-trip, zero configuration."
---

The N+1 query problem is the most common performance issue in GraphQL APIs. BifrostQL solves it at the architecture level — no DataLoader, no manual batching, no per-resolver wiring.

## What is the N+1 problem?

When a GraphQL resolver fetches a list and then resolves a related field for each item, the database sees 1 query for the list plus N queries for the related rows.

```graphql
{
  orders(limit: 50) {
    data {
      orderId
      __join {
        customers {
          data { name }
        }
      }
    }
  }
}
```

A naive resolver executes:

1. `SELECT * FROM orders LIMIT 50` — 1 query
2. `SELECT * FROM customers WHERE customerId = @id` — repeated 50 times

That's **51 database round-trips** for a single GraphQL request. Add another joined table and it doubles. Nest three levels deep and it compounds further.

The cost isn't just latency. Each round-trip carries connection overhead, query parsing, and execution plan generation. Under load, N+1 patterns exhaust connection pools and saturate database CPU.

## How most tools address it

### DataLoader (batching and caching)

The standard approach, introduced by Facebook. DataLoader collects all keys requested during a single tick of the event loop, then issues one batched query per relationship depth.

```
Tick 1: SELECT * FROM orders LIMIT 50               → 1 query
Tick 2: SELECT * FROM customers WHERE id IN (...)    → 1 query (batched)
```

This reduces 51 queries to 2. But each nesting level adds another tick and another round-trip. A four-level deep query still makes four separate database calls. And every resolver that touches a database needs a DataLoader wired up — it's manual work that scales with your schema.

### Query compilation (GraphQL-to-SQL)

Tools like Hasura and PostGraphile take the opposite approach. They inspect the full GraphQL AST and compile it into a single SQL query with JOINs before any execution happens.

This eliminates N+1 entirely for supported databases, but ties you to a specific database engine and a specific query strategy. Complex queries can produce deeply nested SQL JOINs that the query planner struggles to optimize.

### ORM-level solutions

ORMs like Prisma offer eager loading (`include()`) and relation load strategies. Entity Framework has `.Include()`. These help but depend on the developer choosing the right loading strategy for each query path, and they don't integrate with GraphQL resolvers automatically.

## How BifrostQL solves it

BifrostQL uses a **query-per-table** approach with **single round-trip execution**. It sits between DataLoader-style batching and full query compilation, combining the strengths of both.

### Step 1: Parse the full query tree

Before executing anything, BifrostQL parses the entire GraphQL request into a `GqlObjectQuery` tree structure. This gives it visibility into every table, join, filter, and sort across the entire request.

### Step 2: Generate one SQL statement per table

For each table referenced in the query, BifrostQL generates a single parameterized SQL statement. Join queries use correlated subqueries to scope child rows to their parents:

```sql
-- Query 1: Parent table
SELECT [orderId], [customerId] FROM [orders]
  WHERE ... ORDER BY ... OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY;

-- Query 2: Count
SELECT COUNT(*) FROM [orders] WHERE ...;

-- Query 3: Joined table (customers scoped to parent rows)
SELECT [a].[JoinId] [src_id], [b].[customerId], [b].[name]
  FROM (SELECT DISTINCT [customerId] AS [JoinId] FROM [orders] WHERE ...)
  [a] INNER JOIN [customers] [b] ON [a].[JoinId] = [b].[customerId];
```

The subquery `SELECT DISTINCT [customerId] AS [JoinId] FROM [orders]` ensures only the customers referenced by the parent result set are fetched — no over-fetching, no under-fetching.

### Step 3: Execute all statements in one round-trip

All generated SQL statements are concatenated and sent to the database as a single command:

```csharp
command.CommandText = string.Join(";\r\n", sqlStatements);
using var reader = command.ExecuteReader();
```

The database returns multiple result sets from one `ExecuteReader` call. BifrostQL reads each result set sequentially and maps it back to the corresponding table in the query tree.

### Step 4: Assemble the response in memory

The `ReaderEnum` class stitches result sets together, matching child rows to parent rows using the `src_id` column generated in the join query. The final GraphQL response is assembled without any additional database calls.

## What this means in practice

For the query at the top of this page, BifrostQL generates **2 SQL statements** (orders + customers) plus a count query, all in **1 database round-trip**.

| Nesting depth | Tables queried | Naive resolvers | DataLoader | BifrostQL |
|--------------|---------------|----------------|------------|-----------|
| 1 level | orders → customers | 1 + N | 2 round-trips | 1 round-trip |
| 2 levels | orders → customers → addresses | 1 + N + N² | 3 round-trips | 1 round-trip |
| 3 levels | orders → customers → addresses → cities | 1 + N + N² + N³ | 4 round-trips | 1 round-trip |

The number of SQL statements scales with the number of **tables** in the query, not the number of **rows**. And all statements execute in a single round-trip regardless of depth.

## Comparison with other GraphQL tools

| Tool | Platform | N+1 Strategy | Setup required | DB round-trips | Trade-offs |
|------|----------|-------------|----------------|---------------|------------|
| **DataLoader** | Any (JS, .NET, etc.) | Batch keys per resolver tick | One DataLoader per relationship | 1 per nesting level | Manual wiring; still multiple round-trips for deep queries |
| **Hasura** | Haskell / Docker | Compile full GraphQL AST to single SQL with JOINs | Zero (schema-driven) | 1 | PostgreSQL-centric; complex queries produce deeply nested JOINs |
| **PostGraphile** | Node.js | Look-ahead (V4) / Grafast planning (V5) | Zero (schema-driven) | 1 | PostgreSQL-only; V5 is a significant rewrite |
| **Join Monster** | Node.js | Schema annotations generate SQL JOINs from AST | Medium — annotate every type | 1-2 | JS-only; large JOINs risk memory issues; community-maintained |
| **Prisma** | Node.js | Internal DataLoader + optional join strategy | Low-Medium — use `include()` | 1-2 | Tied to Prisma ORM; join strategy is newer |
| **Hot Chocolate** | .NET | DataLoader + Projections | Medium — register DataLoaders | 1 per nesting level | DataLoaders and projections don't fully integrate ([#6191](https://github.com/ChilliCream/graphql-platform/issues/6191)) |
| **BifrostQL** | .NET | Query-per-table, single round-trip batch | **Zero** — reads DB schema | **1**\* | SQL Server, PostgreSQL, MySQL, SQLite\*; .NET ecosystem |

### BifrostQL vs DataLoader

DataLoader is the standard N+1 solution, but it requires explicit setup. Every relationship needs a DataLoader registered, keys defined, and batch functions written. Miss one and you're back to N+1. DataLoader also can't eliminate the multiple-round-trip cost of deeply nested queries — each level of nesting is a separate database call.

BifrostQL requires no DataLoader layer at all. The query tree is analyzed upfront, and all SQL is generated and executed before any resolver runs.

### BifrostQL vs Hasura / PostGraphile

Hasura and PostGraphile take a similar philosophy — compile GraphQL to SQL from the database schema. The key differences:

- **Database support**: Hasura and PostGraphile are PostgreSQL-first. BifrostQL supports SQL Server, PostgreSQL, and MySQL.
- **Query strategy**: Hasura compiles to a single SQL statement with nested JOINs. BifrostQL generates separate statements per table (avoiding deeply nested JOINs that can confuse query planners) but sends them all in one round-trip.
- **Platform**: Hasura is a standalone Docker service. PostGraphile is Node.js. BifrostQL is a .NET library that embeds into your ASP.NET Core app.

### BifrostQL vs Hot Chocolate

Hot Chocolate is the most popular GraphQL library in .NET. It provides DataLoader support through GreenDonut and projections through IQueryable. But:

- DataLoaders must be manually registered for each relationship
- Projections and DataLoaders don't fully integrate — you often end up choosing one or the other
- Each nesting level is still a separate database round-trip

BifrostQL is zero-configuration. Point it at a database and the entire API, including optimized query execution, is generated automatically.

## When query-per-table is the right approach

BifrostQL's strategy works best when:

- **You need zero-config GraphQL** from an existing database
- **Your queries join 2-5 tables** at moderate depth — the common case for application APIs
- **You want predictable database load** — the number of SQL statements is determined by schema structure, not data volume
- **You're in the .NET ecosystem** and want a library, not a separate service

For extremely wide queries that touch dozens of tables in a single request, the number of SQL statements grows linearly. In practice, real application queries rarely exceed 5-6 joined tables, making this a non-issue.

\* **SQLite limitation**: SQLite's ADO.NET provider doesn't support multiple result sets from a single `ExecuteReader` call. For SQLite, BifrostQL executes each statement in a separate round-trip, making it equivalent to DataLoader's per-level approach. The N+1 problem is still eliminated (one query per table, not per row), but deep queries incur multiple round-trips.
