# BifrostQL Agent Guide

BifrostQL is a .NET library that publishes a SQL database as a GraphQL API, building the GraphQL schema from the database schema. Non-GraphQL front doors (pgwire, RESP, LDAP, gRPC, OData, S3, MCP, feeds) run on the same pipeline.

This file holds only rules that change what you do. The incidents behind them live in `docs/solutions/bifrostql/` and git history (full pre-trim text: `git show 58d13dfb:AGENTS.md`). Most rules name a single home and the test that pins it: extend the home, never write a second copy.

## Build & Test

```bash
dotnet build BifrostQL.sln
dotnet test                                   # epic tier
dotnet test --filter "FullyQualifiedName=TestName"
scripts/test-epic.sh                          # epic tier, excludes Category=Fuzz
scripts/test-release.sh                       # release tier: net8/9/10 + Fuzz (-p:ReleaseTests=true)
./dev-ui.sh [--port 5000]                     # desktop UI dev mode (edit-db watch + backend + Vite)
dotnet run --project src/BifrostQL.Host       # web server
./scripts/kill-dev-processes.sh [--kill]      # stray dev processes for this checkout (dry-run default)
```

- Epic tier builds net10.0 only. If you touch `src/BifrostQL.Server/`, `src/BifrostQL.Core/` or `src/BifrostQL.Abstractions/` (multi-TFM), run `dotnet build BifrostQL.sln` before finishing: net9+-only BCL APIs pass epic and break release.
- Tag fuzz tests `[Trait("Category", "Fuzz")]` with pinned `InlineData` seeds.
- Tests: xUnit + NSubstitute + FluentAssertions; SQL validation via `Microsoft.SqlServer.TransactSql.ScriptDom`.

## Generated output

- `src/BifrostQL.UI/wwwroot` is Vite output of `src/BifrostQL.UI/frontend`. Never hand-edit; `pnpm --dir src/BifrostQL.UI/frontend build`.
- `samples/HostedSpa/wwwroot` is committed on purpose (`HostedSpaSmokeTests` serves it, no pnpm step). After changing `samples/HostedSpa/spa/src`, run `pnpm --dir samples/HostedSpa/spa build` and commit the regenerated `wwwroot/`.
- `bin`, `obj`, `node_modules`, `dist`, coverage and Storybook output are artifacts.

## Package manager

pnpm 11.1.1 (root `packageManager`), one root `pnpm-lock.yaml` for every workspace package including docs. No `package-lock.json` or nested lockfiles. Use `pnpm --dir <pkg>` / `pnpm --filter <pkg>`.

## Request flow

`BifrostHttpMiddleware` → `BifrostDocumentExecutor` (cached `DbModel` + `ISchema`) → `SqlVisitor` builds the `GqlObjectQuery` tree → filter/mutation transformers → `GqlObjectQuery.AddSqlParameterized()` → `SqlExecutionManager` → response.

Only table root fields enter the `GqlObjectQuery` tree. Other root fields (introspection, `<t>Aggregate`/`Pivot`/`History`, `_rawQuery`, `_dbSchema`) have their own resolvers. Sibling root resolvers share one parse task, so a throw in the builder faults the whole document.

## Core rules

- **Selection-derived SQL** walks `context.FieldAst.SelectionSet` through `Resolvers/SelectionWalk.cs`. Never read `IResolveFieldContext.SubFields`: it is keyed by alias and loses columns when one field has ≥2 aliases.
- **Table identity is `IDbTable`, not a bare `DbName`.** One name can exist in two schemas. Thread the resolved `IDbTable` through. Client-supplied names: reads use schema-qualified `GetTableFromDbName(schema, name)` or `TryGet*`; writes use `IDbModel.TryGetTableFromClientName`. A miss goes through `BifrostErrorSink.LookupMiss`, and the wire text carries no name. Pinned by `ReadPathTableIdentityHygieneTests`.
- **Keyed writes:** predicate columns come from `TableMutationPipeline.SelectPredicateColumns` (pre-chain). The key/SET split lives in `MutationArgumentBinder`. Statement text and the zero-row policy (`EnsureAffectedRows`) live in `MutationCommandExecutor`. Pinned by `KeyedWriteSourceScanTests`.
- **Row ceiling:** every row read goes through `GqlObjectQuery.ResolveRowWindow`. Pass the raw `Limit` and never add `?? DefaultRowWindow`. A flat collection passes `-1` explicitly. The cap goes in the query with an ORDER BY; client-side `.Take(n)` is not a cap. Report truncation with a cap+1 sentinel in the surface's own response envelope. When auditing a limit guard, check for a `return` above the call site.
- **Mutation input normalisation** goes in `MutationTransformersWrap.TransformAsync`, the one pre-chain seam every write path uses. Not `MutationArgumentBinder`: insert and delete bypass it.
- **Write events/observers** fire only when `AffectedRows > 0`. Never gate on the scalar `Value`: on a single-column-PK table it is the key.
- **Hook gates** ask applicability (`AppliesTo(IDbTable)` / `AnyApplies`), never registration. The registrar adds every hook to every host.
- **Config validators** call the runtime parse function (`BatchMutationPipeline.GetMaxBatchSize`, `HistoryConfig.FromTable`, …), never a second parser.
- **Hook state:** `MutationState` is per action, `TransactionState` is per transaction. Per-action state left in the transaction bag leaks into the next row.
- **`BifrostErrorSink`** is the one sanitize-the-wire/log-the-detail seam. Attach a logger with `BifrostErrorSink.Attach`, never `??=`.

## Protocol adapters

An adapter owns only the wire and the codec. Reads go through `IQueryIntentExecutor`, writes through `IMutationIntentExecutor`, identity through `IBifrostAuthContextFactory` (fail-closed). Non-HTTP adapters host on a Kestrel `ConnectionHandler` + `IHostedService`. Security checklist: `.claude/rules/protocol-adapter-security.md` (invariants are cited by number in code comments).

### Listener Exposure Posture

Undeclared means `loopback`. Widening (loopback → lan → public) is the operator's decision, never an agent's. Shipped defaults:

| Listener | Port | Posture | Max connections | Pre-auth deadline | Idle | Max message |
|---|---|---|---|---|---|---|
| pgwire | 5432 | loopback (`PgWireOptions.BindAddress`) | `MaxConnections` 100 | `HandshakeTimeout` 30 s; SCRAM `MaxAuthAttemptsPerSource` 100 / 1 min | none | 1 MiB |
| RESP | 6379 | loopback (`RespWireOptions.BindAddress`) | 100, taken before TLS | `AuthenticationTimeout` 30 s | `IdleTimeout` 10 min | `MaxBulkLength` / `MaxFrameLength` 1 MiB |
| LDAP / LDAPS | 389 / `LdapsPort` (null = off) | loopback (`LdapWireOptions.BindAddress`) | 100 shared across both | `AuthenticationTimeout` 30 s, `TlsHandshakeTimeout` 30 s | `IdleTimeout` 5 min (credentialed only) | 1 MiB |
| gRPC | 5090 | loopback (`GrpcWireOptions.BindAddress`) | `MaxConcurrentConnections` 100 | Kestrel HTTP/2 | Kestrel | Kestrel |
| HTTP (GraphQL, S3, OData, MCP-HTTP, Prometheus) | host Kestrel | host's | host | host | host | host |
| Binary WS `/bifrost-ws` | host Kestrel | host's | `UseBifrostBinary(maxConnections:)` 100 per mount | `firstFrameTimeout` 30 s | `idleTimeout` 10 min | frame 4 MiB; reassembly 64 MiB/session, 128 MiB/connection (received bytes) |
| LLM chat `/_chat` | host Kestrel | host's | host | identity gate before body read | one SSE stream per conversation | `MaxMessageLength` 32768 chars; body 4×that + 1 KiB (received bytes) |
| Desktop UI `bifrostui` | `--port` | loopback `127.0.0.1`; `--expose` widens to LAN | host | none: loopback is the only gate | host | host |

`bifrostui --enable-http-bridge` (`/_bridge/{kind}`) runs SQL with no auth. It refuses to start alongside `--expose` (exit 2), and every POST needs `X-Bifrost-Bridge: 1` + `Content-Type: application/json`. The header is the load-bearing half.

Every new front door must:

- Bind `options.BindAddress` (loopback default), never `ListenAnyIP`. Pinned by `ProtocolListenerPostureTests`.
- Take its admission slot at accept, before read/TLS/auth, from its own `ProtocolConnectionLimiter` subtype.
- Run its session through `ProtocolSessionHost` (acquire → pre-auth deadline → run → release; `RunAdmittedAsync` still arms the deadline). The deadline is `TimeProvider`-driven. Only a credentialed action retires it, and free actions never re-arm it (invariant 15).
- Cap whole frames by total bytes (`MaxFrameLength`), not just per element. Every size cap counts received bytes, never declared ones.
- Rate-limit credential handshakes per source (`ProtocolSourceKey.Of`, IP not ip:port) and per account, via a sealed `ProtocolAuthAttemptLimiter` subtype. Refuse before the credential lookup, with text that doesn't depend on whether the account exists.
- Cap per-session state (e.g. pgwire `MaxPreparedStatements` / `MaxPortals`).
- Project identity through `AdapterIdentityProjection.TryProject` (raw wire) or `BifrostIdentityGate.Project` (HTTP mounts). An empty user context is not a refusal.
- Refuse credentials on cleartext transport before reading them. A dev override is default-off with a startup warning; StartTLS accepts no buffered plaintext.
- Bound searches/reads per request. Clients may only narrow the bounds. Report limits explicitly, never truncate silently, and apply join fan-out bounds per entry.
- MAC continuation cookies and re-derive their binding from the live request. An invalid cookie is refused, never restarted (`LdapPageCookie`, `ODataContinuationToken`, `GrpcPageCursor`).
- Use `RegexOptions.NonBacktracking` + `Regex.InfiniteMatchTimeout` for untrusted regex on hot paths (invariant 1).
- HTTP mounts derive their auth requirement from `MountAuthRequirement` as a required constructor parameter, and mount after `UseBifrostQL()` / `UseBifrostEndpoints()`. Mount order is a security fact; review order changes as security diffs.

## Key components

| Component | Location |
|---|---|
| `DbModel` (pure schema data) | `Model/DbModel.cs` |
| `TableRelationshipOrchestrator` | `Model/Relationships/` |
| `GqlObjectQuery` (query tree → SQL) | `QueryModel/GqlObjectQuery.cs` |
| `ISqlDialect` | `QueryModel/ISqlDialect.cs` |
| `ResolverBase` | `Resolvers/ResolverBase.cs` |
| `MetadataKeys`, `StringNormalizer` | `Model/MetadataKeys.cs`, `Utils/StringNormalizer.cs` |
| `AppMetadataModel` (client presentation overlay, never merged into schema metadata) | `AppMetadata/` |
| `IProtocolAdapter` (`AddProtocolAdapter<T>`), `ProtocolSessionHost` | `BifrostQL.Server/` |
| `BifrostMcpAdapter`: `McpAuthOptions.Mode` defaults to `FailClosed`, so snippets must declare a mode | `BifrostQL.Mcp/` |
| `IQueryIntentExecutor`, `IMutationIntentExecutor` | `Resolvers/` |
| `ProtocolAdapterConformanceTests` (derive per adapter; opt-in `AdapterSupports*` flags) | `tests/BifrostQL.AdapterConformance/` |

## Extension points

- Dialects: `SqlDialectBase` (SQL Server `[x]`, `+`), `StandardConcatDialectBase` (Postgres/SQLite `"x"`, `||`), `LimitOffsetDialectBase` (MySQL `` `x` ``, `CONCAT()`).
- Filters `SingleColumnFilterTransformerBase`, mutations `MetadataMutationTransformerBase`, resolvers `TableResolverBase`, observers `IQueryObserver`. Priority: 0–99 security, 100–199 data filtering, 200+ app.
- Metadata keys live only in `MetadataKeys`. A new key means updating `MetadataKeys`, validation allow-lists, docs and tests. Tenant and soft-delete keys are security-critical.
- No user input in SQL text, no sync I/O in resolvers, no magic strings, `StringNormalizer` instead of `ToLowerInvariant().Trim()`.

## Clients

- **Two Client Stacks:** the shipped chain is `src/BifrostQL.UI/frontend` → `@standardbeagle/edit-db`. `@bifrostql/react` and `@bifrostql/app-shell` are experimental. The three fetch clients (`graphql-client.ts`, edit-db `fetcher.ts`, UI `HttpTransport`) are known duplicates; don't merge them ad hoc. Unification targets `QueryTransport` in `frontend/src/lib/transport.ts`.
- `@bifrostql/types` + `@bifrostql/react` publish as a matched pair. Breaking changes go under `### Breaking changes` in the react CHANGELOG.
- Editor data paths go through `useFetcher()`; the transport toggle routes them via `TransportGraphQLFetcher`. Don't bump `editorKey` on a profile change.
- New state in `use-bifrost-table` goes in a focused hook under `hooks/internal/`.
- GraphQL text: never interpolate user-provided names. Filter input type names come from `tableFilterTypeName(table)`; sort enums are `<GraphQlName>SortEnum`. Composite keys: `.claude/rules/composite-pk-compliance.md`.

## Docs

Canonical user docs: `docs/src/content/docs`. `docs-research` is exploratory and may be stale. When a change retires a mechanism, follow `.claude/rules/steering-docs-follow-mechanism-changes.md`.
