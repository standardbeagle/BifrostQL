---
title: "Bulk Batch Performance"
description: "Measured throughput of the set-based batch fast path on SQL Server, PostgreSQL, and MySQL — per-row vs bulk vs the raw driver floor — and how to tune it."
---

BifrostQL batch mutations (`<table>_batch`, and `IMutationIntentExecutor.ExecuteBatchAsync` used by the protocol adapters) take a set-based fast path on SQL Server, PostgreSQL, and MySQL: every row runs the full mutation transformer chain, the transformed rows are staged into a temp table (streamed where the engine allows — see below), and the whole batch applies as set-based INSERT/UPDATE/DELETE statements inside one SQL-level transaction. This page reports measured throughput of that path so you can decide when to use it and what to expect.

Where this wins over reaching for a raw bulk API yourself: native bulk loaders (`SqlBulkCopy`, `COPY`, `LOAD DATA`) only *insert*, and they bypass every policy. BifrostQL's batch path takes **mixed insert/update/delete in one transaction** — the shape of a grid edit saving fifty touched rows, or an hourly sync reconciling two systems — at bulk-load-class throughput, with tenancy, policy, soft-delete, audit, and optimistic-concurrency semantics applied per row.

## Measured results

Method: median of 5 iterations after 1 warmup, wall clock, via `dotnet run -c Release -- --bulk-paths` in `benchmarks/BifrostQL.Benchmarks`. Four-column table (`INT` PK, `INT`, `VARCHAR(100)`, `DECIMAL(18,2)`), no hooks/tenancy configured — transformer-heavy tables (tenancy, policy, encryption) add per-row CPU that shifts bulk numbers down somewhat, and hook-bearing tables use the per-row path entirely (see "When the fast path runs"). Environment: one machine (WSL2, 12 cores, load average 3–5 during the run), database servers in local Docker containers (`mcr.microsoft.com/mssql/server:2022-latest`, `postgres:16`, `mysql:8.4` with `--local-infile=1` and `AllowLoadLocalInfile=true` on the connection string, which the MySQL floor and streaming load need), .NET 10. Measured 2026-11-20 on `main` after the insert-run change. **Treat the numbers as relative, not absolute** — loopback networking flatters round-trip costs, and your hardware, network, and schema will shift everything. Re-run the harness against your own environment for capacity planning.

The three series:

- **per-row** — the batch pipeline with the fast path disabled (`bulk-batch-threshold: 0`): one statement per row on a shared transaction. Every deployment gets at least this. On PostgreSQL the per-row loop writes a run of adjacent inserts as one multi-row `INSERT … RETURNING` (see [Insert runs on the per-row loop](#insert-runs-on-the-per-row-loop)), so its "insert per-row" rows are one statement per run, not per row. An A/B/A/B run against the build before that change, on the same machine in the same session, gave 1,000 per-row inserts 1,055–1,127 ms before and 51–53 ms after (about 21x), and per-row updates (1,082–1,136 ms before, 1,116–1,121 ms after) and deletes (1,058–1,068 ms before, 1,024–1,050 ms after) unchanged.
- **bulk** — the set-based fast path (full transformer chain per row, staging, set-based apply).
- **floor** — the provider's native bulk API (`SqlBulkCopy`, binary `COPY`, `MySqlBulkCopy`) writing straight into the target table with **no** pipeline, transactions-per-op, or result accounting. This is the theoretical ceiling; the gap between *bulk* and *floor* is the price of the security/consistency pipeline.

| Provider | Scenario | Rows | Median ms | Rows/s |
|----------|----------|------|-----------|--------|
| SQL Server | insert per-row | 100 | 173.5 | 577 |
| SQL Server | insert per-row | 1,000 | 1319.4 | 758 |
| SQL Server | insert bulk | 100 | 62.7 | 1,595 |
| SQL Server | insert bulk | 1,000 | 72.8 | 13,732 |
| SQL Server | insert bulk | 10,000 | 268.4 | 37,258 |
| SQL Server | insert floor | 1,000 | 19.8 | 50,505 |
| SQL Server | insert floor | 10,000 | 99.1 | 100,882 |
| SQL Server | update per-row | 1,000 | 1274.9 | 784 |
| SQL Server | update bulk | 1,000 | 120.6 | 8,295 |
| SQL Server | delete per-row | 1,000 | 1272.4 | 786 |
| SQL Server | delete bulk | 1,000 | 125.3 | 7,983 |
| SQL Server | insert graphql+bulk | 1,000 | 143.5 | 6,969 |
| PostgreSQL | insert per-row | 100 | 7.2 | 13,906 |
| PostgreSQL | insert per-row | 1,000 | 35.5 | 28,200 |
| PostgreSQL | insert bulk | 100 | 26.0 | 3,851 |
| PostgreSQL | insert bulk | 1,000 | 52.0 | 19,227 |
| PostgreSQL | insert bulk | 10,000 | 195.1 | 51,267 |
| PostgreSQL | insert floor | 1,000 | 9.5 | 105,332 |
| PostgreSQL | insert floor | 10,000 | 43.4 | 230,235 |
| PostgreSQL | update per-row | 1,000 | 1101.3 | 908 |
| PostgreSQL | update bulk | 1,000 | 46.5 | 21,490 |
| PostgreSQL | delete per-row | 1,000 | 1044.9 | 957 |
| PostgreSQL | delete bulk | 1,000 | 35.0 | 28,606 |
| PostgreSQL | insert graphql+bulk | 1,000 | 74.1 | 13,499 |
| MySQL | insert per-row | 100 | 199.4 | 501 |
| MySQL | insert per-row | 1,000 | 1322.5 | 756 |
| MySQL | insert bulk | 100 | 24.8 | 4,035 |
| MySQL | insert bulk | 1,000 | 44.7 | 22,394 |
| MySQL | insert bulk | 10,000 | 229.8 | 43,521 |
| MySQL | insert floor | 1,000 | 26.9 | 37,145 |
| MySQL | insert floor | 10,000 | 94.1 | 106,292 |
| MySQL | update per-row | 1,000 | 1345.4 | 743 |
| MySQL | update bulk | 1,000 | 59.7 | 16,746 |
| MySQL | delete per-row | 1,000 | 1249.3 | 800 |
| MySQL | delete bulk | 1,000 | 52.2 | 19,143 |
| MySQL | insert graphql+bulk | 1,000 | 106.5 | 9,390 |

## Reading the numbers

- **The fast path is 18–30x the per-row path for inserts at 1,000 rows on SQL Server and MySQL** (18x SQL Server, 30x MySQL) and keeps climbing with batch size — the per-row path is flat (round trips dominate) while the bulk path amortizes its fixed cost. On PostgreSQL the per-row loop writes an insert run as one statement too, and at 100 and 1,000 rows it beat the bulk path (35.5 ms against 52.0 ms at 1,000), which pays for staging.
- **The pipeline reaches roughly 22–41% of the raw floor at 10,000 rows** (bulk/floor: 37% SQL Server, 22% PostgreSQL, 41% MySQL). That gap is real work the floor skips: the per-row transformer chain, staging-table DDL, the transactional set-based apply with joins, the conflict probe, and per-row affected accounting. If you need the floor and none of the pipeline, BifrostQL is the wrong tool for that one load — this path exists to give you MOST of the floor **with** tenancy, policy, soft-delete, and concurrency semantics intact.
- **GraphQL document parsing is the tax on very large `_batch` mutations.** At 1,000 actions the GraphQL layer added roughly 20–70ms on top of the pipeline in our runs (this number is the noisiest in the suite). Server-side callers and protocol adapters (RESP, gRPC, MCP) go through `IMutationIntentExecutor` and skip document parsing entirely; prefer that seam for machine-generated bulk traffic.
- **Updates and deletes gain 10–30x at 1,000 rows** (SQL Server ~10x, PostgreSQL 24–30x, MySQL 22–24x) — the staged join replaces a thousand keyed statements.
- **PostgreSQL staging is ANALYZEd after load by design.** Without statistics on the freshly-filled temp table, the planner's join choice for the set-based UPDATE/DELETE was a coin flip between a hash join (~30ms) and a catastrophic nested loop (~200ms). The executor runs `ANALYZE <staging>` after every load, which pinned the fast plan across repeated runs; the ~1ms it costs is included in the numbers above.

## When the fast path runs

The fast path engages when **all** of these hold; anything else falls back to the per-row loop (the same semantics, apart from the [exception below](#how-the-fast-path-keeps-document-order), just slower — falling back is normal operation):

- the provider has a bulk executor (SQL Server, PostgreSQL, MySQL — SQLite deliberately stays per-row: a single in-process writer has no round trips to save),
- the batch has at least `bulk-batch-threshold` actions (default 50; `0` disables per table) and at most `batch-max-size`,
- no before-commit / in-transaction mutation hook **applies to the table** (approval workflows, change history, CDC outbox need per-row before-images and identities). Applicability, not registration: every host registers the built-in hooks unconditionally and each one no-ops for tables that did not opt in, so the gate asks whether a registered hook can act on *this* table. A hook that does not declare applicability counts as applying, so an unrecognised third-party hook still forces the per-row path,
- no upsert actions (the upsert existence probe is per-row by design),
- the table has no state machine,
- every row's transformer filter (tenant scope, policy row scope, soft-delete guard) renders identically — true for normal tenant/policy filters; per-row-varying filters (e.g. optimistic-concurrency tokens at *different* versions in one batch) fall back,
- duplicate update/delete keys never reach either path raw: `batch-duplicate-policy` resolves them deterministically in Core before any SQL — `last-wins` (default) collapses them to their sequential net effect (update+update merges per column, a delete absorbs updates on its key; collapsed intermediates never execute, so hooks/history never see them), `reject` refuses the batch. A collision involving an upsert is always refused (its net effect depends on row existence). On a PostgreSQL `uuid` or SQL Server `uniqueidentifier` column, two spellings of one uuid (case, hyphens, braces) are one key on both paths, by `GeneratedBatch_TreatsTwoSpellingsOfAUuidAsOneRow_OnBothPaths`; that includes a hyphenless spelling SQL Server itself refuses. SQLite keeps a column declared `UUID` as text and MySQL has no uuid type, so each spelling is its own key there, by `SqliteDeclaredUuidColumnTests`. Every other key compares as text, so a `citext` or case-insensitive-collation key differing only in case, or a numeric key written `1.0` and `1.00`, is not seen as a duplicate: on the fast path two actions reach one row, the database picks the winner and the count reads 2.
- the document has at most 8 runs (see below).

## How the fast path keeps document order

The fast path merges only **adjacent** rows with the same verb, the same columns and the same filter. Each such run is one set-based statement, and the runs apply in document order, so `[insert x, delete y, insert y]` is three statements and gives the per-row result. `ContractDocument_MatchesThePerRowOutcome`, `InsertDeleteInsert_AppliesTheLaterInsertAfterTheDelete` and `UpdateThatFreesAValue_ThenInsertThatReusesIt_MatchesThePerRowOutcome` compare both paths on PostgreSQL, and `InsertDeleteInsert_ReusingTheFreedKey_ProducesIdenticalOutcomes` does on PostgreSQL, MySQL and SQL Server.

**What this costs.** One statement per run, so the fast path pays off when runs are long. 10,000 rows of one verb and one column set are one statement. A batch that changes verb or column set on every row is one statement per row, and past 8 runs the whole batch takes the per-row loop, so it gets no speedup. That includes an insert-only batch that alternates an optional column. Send rows with the same columns next to each other if the batch has a choice.

**The exception.** Constraints are checked per statement, and a run is one statement on the fast path and one per row on the per-row path (except a run of inserts on PostgreSQL, below). Inside a run the two can part:

- A foreign key is checked when the statement ends. Deletes that satisfy it only together, such as `[delete parent, delete child]` on a self-referencing table, apply on the fast path and fail per row, with `FOREIGN_KEY_VIOLATION` on PostgreSQL and `DATABASE_ERROR` on a dialect that reports no SQLSTATE. Inserts that satisfy it only together, such as `[insert child, insert parent]`, apply on both paths on PostgreSQL, where the per-row loop writes the run as one statement too, and fail per row on the other dialects. `RowsThatSatisfyAForeignKeyOnlyTogether_PassAsOneRun_ButNotRowByRow` pins both on PostgreSQL. The key holds afterwards either way. Nothing tells a caller which path ran, so a document that relies on this works only above the threshold, or for inserts on PostgreSQL.
- A non-deferrable unique index is checked per row, in the order the query plan visits them, which the statement does not fix to document order. Update chains that hand a value down (`[a -> z, b -> a]`, and the same in reverse) matched the per-row result in every case we measured, up to 5,000 rows on PostgreSQL, but no test can pin a planner's choice. A document that depends on the order inside one update run should set `bulk-batch-threshold: 0` on that table.

## Insert runs on the per-row loop

On a database that returns a multi-row insert's keys in row order (PostgreSQL), the per-row loop writes a run of adjacent inserts as one `INSERT … VALUES (…),(…) RETURNING <keys>` per column set, inside the batch's transaction. The transformer chain and the before-commit hooks still run once per row, before the statement, and a refusal of any row refuses the batch. The run is split only when an in-transaction hook that is not set-aware applies to the table, because such a hook is called before the next row's statement. A hook that implements `IInTransactionSetMutationHook` gets every row once the batch is written, so a table whose hooks are all set-aware keeps its insert runs in one statement. The same loop writes an operation's `DeltaAsync`, inside the operation's transaction (see [server operations](/guides/server-operations/)).

## Streaming staging loads

The staging load itself streams where the engine allows, and falls back to chunked parameterized INSERTs otherwise — streaming is a performance strategy, never a semantics change (a streamed load that fails or warns is discarded and re-run parameterized):

| Provider | Streaming API | Requirement |
|----------|---------------|-------------|
| SQL Server | `SqlBulkCopy` (TDS bulk load) | none — always streams |
| PostgreSQL | binary `COPY` | none — always streams; falls back if a column type is unmappable |
| MySQL | `MySqlBulkCopy` (`LOAD DATA LOCAL INFILE`) | `AllowLoadLocalInfile=true` in the connection string **and** `local_infile=1` on the server; otherwise chunked INSERTs (still fast) |

On PostgreSQL, binary `COPY` refuses text for most non-text columns, and the chunked load then fails too, so the batch takes the per-row loop with no error in the reply (the log carries a warning, below). Measured with text values, as GraphQL sends them, on `date`, `timestamp`, `timestamptz`, `time`, `interval`, `inet`, `cidr`, `macaddr`, `bytea` and `money`. Canonical `uuid` text is converted and stays on the fast path, as `GeneratedBatch_WithTextKeys_StaysOneStatementPerRun` pins.

Every fallback is logged under the `BifrostQL.BulkBatch` category, so you can see which load ran:

- **Debug** `Bulk batch for <table> streamed <n> rows into staging`: the stream ran.
- **Warning** `Bulk batch for <table> loaded <n> rows with the chunked INSERT, not the streaming load: <reason>`: the chunked load ran. The reason names the cause, for example a MySQL connection string without `AllowLoadLocalInfile=true`, or a server with `local_infile` off ("Loading local data is disabled").
- **Warning** `Bulk batch staging failed for <table>; running the <n>-row batch on the per-row path instead`: staging failed outright, with the exception attached.
- **Debug** `Bulk batch fast path skipped for <table>: <reason>`: the batch never qualified (see "When the fast path runs").

## Tuning guidance

- Raise `batch-max-size` (default 100) on tables that receive genuine bulk traffic — the fast path's advantage grows with batch size, and 100-row batches leave most of it on the table.
- Lower `bulk-batch-threshold` (default 50) toward 1 if your batches are small but frequent; set `0` to pin a table to the per-row path.
- On PostgreSQL, an insert-heavy table can raise `bulk-batch-threshold` instead: the per-row loop writes an insert run as one statement with no staging, and it measured faster than the fast path at 100 and 1,000 inserts. Updates and deletes still gain 24–30x from the fast path there, so leave the threshold alone on tables that take mixed batches.
- For machine-driven ingest, prefer the intent API (or a protocol adapter) over GraphQL text to skip document parsing.
- The RESP (Redis-protocol) adapter's multi-key `MSET` and `DEL` route as one batch per table, so a command with ≥ threshold keys rides the fast path automatically (see the RESP guide's write-command table for their semantics).
- Tables with approval/history/CDC hooks always take the per-row loop. On PostgreSQL that loop still writes a run of inserts as one statement when every in-transaction hook on the table is set-aware; the built-in history and CDC hooks are per-row, so their tables write row by row. For bulk loads there, batch into a staging table of your own and promote via a hook-free table, or disable the feature for the load window deliberately.

## Reproducing

```bash
export BIFROST_BENCH_SQLSERVER="Server=...;User Id=...;Password=...;TrustServerCertificate=True"
export BIFROST_BENCH_POSTGRES="Host=...;Username=...;Password=..."
export BIFROST_BENCH_MYSQL="Server=...;Uid=...;Pwd=...;AllowLoadLocalInfile=true"
dotnet run --project benchmarks/BifrostQL.Benchmarks -c Release -- --bulk-paths
```

Each provider gets a throwaway database (`bifrost_bench_<guid>`), dropped afterward. Unset providers are skipped.
