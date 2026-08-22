---
title: "Bulk Batch Performance"
description: "Measured throughput of the set-based batch fast path on SQL Server, PostgreSQL, and MySQL — per-row vs bulk vs the raw driver floor — and how to tune it."
---

BifrostQL batch mutations (`<table>_batch`, and `IMutationIntentExecutor.ExecuteBatchAsync` used by the protocol adapters) take a set-based fast path on SQL Server, PostgreSQL, and MySQL: every row runs the full mutation transformer chain, the transformed rows are staged into a temp table (streamed where the engine allows — see below), and the whole batch applies as set-based INSERT/UPDATE/DELETE statements inside one SQL-level transaction. This page reports measured throughput of that path so you can decide when to use it and what to expect.

## Measured results

Method: median of 5 iterations after 1 warmup, wall clock, via `dotnet run -c Release -- --bulk-paths` in `benchmarks/BifrostQL.Benchmarks`. Four-column table (`INT` PK, `INT`, `VARCHAR(100)`, `DECIMAL(18,2)`), no hooks/tenancy configured — transformer-heavy tables (tenancy, policy, encryption) add per-row CPU that shifts bulk numbers down somewhat, and hook-bearing tables use the per-row path entirely (see "When the fast path runs"). Environment: one machine (WSL2), database servers in local Docker containers (`mcr.microsoft.com/mssql/server:2022`, `postgres:16-alpine`, `mysql:8` with `--local-infile=1`), .NET 10. **Treat the numbers as relative, not absolute** — loopback networking flatters round-trip costs, and your hardware, network, and schema will shift everything. Re-run the harness against your own environment for capacity planning.

The three series:

- **per-row** — the batch pipeline with the fast path disabled (`bulk-batch-threshold: 0`): one statement per row on a shared transaction. Every deployment gets at least this.
- **bulk** — the set-based fast path (full transformer chain per row, staging, set-based apply).
- **floor** — the provider's native bulk API (`SqlBulkCopy`, binary `COPY`, `MySqlBulkCopy`) writing straight into the target table with **no** pipeline, transactions-per-op, or result accounting. This is the theoretical ceiling; the gap between *bulk* and *floor* is the price of the security/consistency pipeline.

| Provider | Scenario | Rows | Median ms | Rows/s |
|----------|----------|------|-----------|--------|
| SQL Server | insert per-row | 100 | 99.5 | 1,005 |
| SQL Server | insert per-row | 1,000 | 717.7 | 1,393 |
| SQL Server | insert bulk | 100 | 37.4 | 2,676 |
| SQL Server | insert bulk | 1,000 | 47.0 | 21,282 |
| SQL Server | insert bulk | 10,000 | 214.6 | 46,591 |
| SQL Server | insert floor | 1,000 | 12.6 | 79,335 |
| SQL Server | insert floor | 10,000 | 90.8 | 110,151 |
| SQL Server | update per-row | 1,000 | 787.4 | 1,270 |
| SQL Server | update bulk | 1,000 | 85.4 | 11,705 |
| SQL Server | delete per-row | 1,000 | 680.5 | 1,469 |
| SQL Server | delete bulk | 1,000 | 60.2 | 16,609 |
| SQL Server | insert graphql+bulk | 1,000 | 217.9 | 4,590 |
| PostgreSQL | insert per-row | 100 | 65.4 | 1,530 |
| PostgreSQL | insert per-row | 1,000 | 482.5 | 2,073 |
| PostgreSQL | insert bulk | 100 | 14.7 | 6,807 |
| PostgreSQL | insert bulk | 1,000 | 35.3 | 28,365 |
| PostgreSQL | insert bulk | 10,000 | 143.6 | 69,630 |
| PostgreSQL | insert floor | 1,000 | 7.6 | 130,762 |
| PostgreSQL | insert floor | 10,000 | 48.7 | 205,327 |
| PostgreSQL | update per-row | 1,000 | 507.8 | 1,969 |
| PostgreSQL | update bulk | 1,000 | 38.6 | 25,881 |
| PostgreSQL | delete per-row | 1,000 | 514.7 | 1,943 |
| PostgreSQL | delete bulk | 1,000 | 27.4 | 36,511 |
| PostgreSQL | insert graphql+bulk | 1,000 | 80.2 | 12,464 |
| MySQL | insert per-row | 100 | 73.7 | 1,357 |
| MySQL | insert per-row | 1,000 | 1072.3 | 933 |
| MySQL | insert bulk | 100 | 17.6 | 5,680 |
| MySQL | insert bulk | 1,000 | 34.5 | 28,960 |
| MySQL | insert bulk | 10,000 | 208.5 | 47,972 |
| MySQL | insert floor | 1,000 | 28.4 | 35,271 |
| MySQL | insert floor | 10,000 | 98.9 | 101,157 |
| MySQL | update per-row | 1,000 | 661.4 | 1,512 |
| MySQL | update bulk | 1,000 | 42.9 | 23,332 |
| MySQL | delete per-row | 1,000 | 571.4 | 1,750 |
| MySQL | delete bulk | 1,000 | 32.2 | 31,043 |
| MySQL | insert graphql+bulk | 1,000 | 138.2 | 7,236 |

## Reading the numbers

- **The fast path is 14–31x the per-row path at 1,000 rows** (inserts: 15x SQL Server, 14x PostgreSQL, 31x MySQL) and keeps climbing with batch size — the per-row path is flat (round trips dominate) while the bulk path amortizes its fixed cost.
- **The pipeline reaches roughly 34–47% of the raw floor at 10,000 rows** (bulk/floor: 42% SQL Server, 34% PostgreSQL, 47% MySQL). That gap is real work the floor skips: the per-row transformer chain, staging-table DDL, the transactional set-based apply with joins, the conflict probe, and per-row affected accounting. If you need the floor and none of the pipeline, BifrostQL is the wrong tool for that one load — this path exists to give you MOST of the floor **with** tenancy, policy, soft-delete, and concurrency semantics intact.
- **GraphQL document parsing is the tax on very large `_batch` mutations.** At 1,000 actions the GraphQL layer added roughly 45–170ms on top of the pipeline in our runs (this number is the noisiest in the suite). Server-side callers and protocol adapters (RESP, gRPC, MCP) go through `IMutationIntentExecutor` and skip document parsing entirely; prefer that seam for machine-generated bulk traffic.
- **Updates and deletes gain as much as inserts** (~9–19x at 1,000 rows) — the staged join replaces a thousand keyed statements.
- **PostgreSQL staging is ANALYZEd after load by design.** Without statistics on the freshly-filled temp table, the planner's join choice for the set-based UPDATE/DELETE was a coin flip between a hash join (~30ms) and a catastrophic nested loop (~200ms). The executor runs `ANALYZE <staging>` after every load, which pinned the fast plan across repeated runs; the ~1ms it costs is included in the numbers above.

## When the fast path runs

The fast path engages when **all** of these hold; anything else falls back to the per-row loop (identical semantics, just slower — falling back is normal operation):

- the provider has a bulk executor (SQL Server, PostgreSQL, MySQL — SQLite deliberately stays per-row: a single in-process writer has no round trips to save),
- the batch has at least `bulk-batch-threshold` actions (default 50; `0` disables per table) and at most `batch-max-size`,
- no before-commit / in-transaction mutation hooks are registered (approval workflows, change history, CDC outbox need per-row before-images and identities),
- no upsert actions (the upsert existence probe is per-row by design),
- the table has no state machine,
- every row's transformer filter (tenant scope, policy row scope, soft-delete guard) renders identically — true for normal tenant/policy filters; per-row-varying filters (e.g. optimistic-concurrency tokens at *different* versions in one batch) fall back.

## Streaming staging loads

The staging load itself streams where the engine allows, and falls back to chunked parameterized INSERTs otherwise — streaming is a performance strategy, never a semantics change (a streamed load that fails or warns is discarded and re-run parameterized):

| Provider | Streaming API | Requirement |
|----------|---------------|-------------|
| SQL Server | `SqlBulkCopy` (TDS bulk load) | none — always streams |
| PostgreSQL | binary `COPY` | none — always streams; falls back if a column type is unmappable |
| MySQL | `MySqlBulkCopy` (`LOAD DATA LOCAL INFILE`) | `AllowLoadLocalInfile=true` in the connection string **and** `local_infile=1` on the server; otherwise chunked INSERTs (still fast) |

## Tuning guidance

- Raise `batch-max-size` (default 100) on tables that receive genuine bulk traffic — the fast path's advantage grows with batch size, and 100-row batches leave most of it on the table.
- Lower `bulk-batch-threshold` (default 50) toward 1 if your batches are small but frequent; set `0` to pin a table to the per-row path.
- For machine-driven ingest, prefer the intent API (or a protocol adapter) over GraphQL text to skip document parsing.
- The RESP (Redis-protocol) adapter's multi-key `DEL` already routes as one batch per table, so a `DEL` of ≥ threshold keys rides the fast path automatically. A multi-key write command (`MSET`) is not yet implemented.
- Tables with approval/history/CDC hooks always take the per-row path today; if you need bulk loads on such tables, batch into a staging table of your own and promote via a hook-free table, or disable the feature for the load window deliberately.

## Reproducing

```bash
export BIFROST_BENCH_SQLSERVER="Server=...;User Id=...;Password=...;TrustServerCertificate=True"
export BIFROST_BENCH_POSTGRES="Host=...;Username=...;Password=..."
export BIFROST_BENCH_MYSQL="Server=...;Uid=...;Pwd=...;AllowLoadLocalInfile=true"
dotnet run --project benchmarks/BifrostQL.Benchmarks -c Release -- --bulk-paths
```

Each provider gets a throwaway database (`bifrost_bench_<guid>`), dropped afterward. Unset providers are skipped.
