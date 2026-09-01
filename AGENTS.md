# BifrostQL Agent Guide

AI 治 BifrostQL，宜循此約。此庫多生成面、字串驅動擴點；凡自動改作，先視此為維護圖。

## Project Overview

BifrostQL 乃 .NET 函庫，以 SQL 資料庫發布為 GraphQL APIs；由資料庫 schema 直建 GraphQL schema。

## Build & Test

```bash
dotnet build BifrostQL.sln
dotnet test
dotnet test --filter "FullyQualifiedName=TestName"
./dev-ui.sh [--port 5000]  # Desktop UI dev mode（edit-db watch + .NET backend + Vite；bifrostui 乃 built assembly 名，非 repo command）
dotnet run --project src/BifrostQL.Host  # Web server
./scripts/kill-dev-processes.sh [--kill]  # 收此 checkout 之遊魂 dev processes（默認 dry-run）
```

### Test tiers: epic vs release

試分二層：

- **Epic tier**（默認，slice/epic loop 與 PR/main CI 用）：test projects 只建當前 TFM（net10.0），Fuzz category 除外。`dotnet test` 素行即此層；`scripts/test-epic.sh` 同，且加 `--filter "Category!=Fuzz"`。
- **Release tier**（release 前 gate，version tag / GitHub release 觸發）：`-p:ReleaseTests=true` 復全 TFM matrix（net8.0/9.0/10.0），且行 Fuzz tests（seeds 皆 pinned `InlineData`，deterministic）。`scripts/test-release.sh` 行之；CI 之 `release-tests` job gate `pack-publish`。

Fuzz tests 標 `[Trait("Category", "Fuzz")]`；新 fuzz-style tests 必同標，否則誤入 epic gate。多 TFM 相容問題（新 BCL API、TFM-conditioned packages）epic 層不見，release 層乃見——release-tests 失敗多屬此類。

## Edit Source, Not Generated Output

- Desktop UI 前端源在 `src/BifrostQL.UI/frontend`。
- `src/BifrostQL.UI/wwwroot` 為該前端 Vite 產物。勿手改 bundled JS、CSS、font files、`index.html`；以 `pnpm --dir src/BifrostQL.UI/frontend build` 重建。
- `src/**/bin`, `src/**/obj`, `node_modules`, package `dist`, coverage, Storybook output 皆 build artifacts。

## Package Manager

- 用 root `packageManager` 所載 pnpm 11.1.1。
- workspace 諸包含 docs，皆用 root `pnpm-lock.yaml`。
- 勿增 `package-lock.json` 或巢狀 pnpm lockfiles，除非該包有意自 `pnpm-workspace.yaml` 移除。
- 宜用 `pnpm --dir <package> <script>` 或 `pnpm --filter <package> <script>`，勝於 `npm`, `npx`, 或 cd 串令。

## Architecture

### Request Flow

1. GraphQL request → `BifrostHttpMiddleware`
2. `BifrostDocumentExecutor` 載 cached `DbModel` + `ISchema`
3. `SqlVisitor` 解析成 `GqlObjectQuery` tree
4. 套 Filter/Mutation transformers
5. SQL 由 `GqlObjectQuery.AddSqlParameterized()` 生
6. `SqlExecutionManager` 執 SQL
7. 結果返為 GraphQL response

非 GraphQL 前門（protocol adapters）：adapter 僅擁 wire + codec。讀經 `IQueryIntentExecutor`（內delegate `SqlExecutionManager.ExecuteIntentAsync`），寫經 `IMutationIntentExecutor`（內 delegate `TableMutationPipeline`）；transformers 於彼二處套，adapter 無 API 可繞。identity 必經 `IBifrostAuthContextFactory`（諸 transport gates 共享，fail-closed）。非 HTTP 宿 Kestrel `ConnectionHandler` + `IHostedService`；contract 無 `HttpContext`。詳 docs concepts/protocol-adapters、guides/protocol-adapters。

### Listener Exposure Posture

每 network listener 必declare exposure 與 concrete caps。**未declare 即 `loopback`**；widening（loopback → lan → public）乃 operator 之決，非 agent 之決。以下為 shipped defaults，非 recommendation ceiling：

| Listener | Port | Posture | Bind default | Max connections | Pre-auth deadline | Idle deadline | Max message |
|----------|------|---------|--------------|-----------------|-------------------|---------------|-------------|
| pgwire | 5432 | `loopback` | `PgWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100 | `HandshakeTimeout` 30 s | none (authenticated session = pooled connection) | `PgProtocolIO.MaxMessageLength` 1 MiB |
| RESP | 6379 | `loopback` | `RespWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100 | `AuthenticationTimeout` 30 s | `IdleTimeout` 10 min | `MaxBulkLength` 1 MiB |
| LDAP | 389 | `loopback` | `LdapWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100（與 LDAPS 共此 counter） | `AuthenticationTimeout` 30 s；`TlsHandshakeTimeout` 30 s | `IdleTimeout` 5 min | `MaxMessageLength` 1 MiB |
| LDAPS | `LdapsPort`（默 null＝off，慣用 636） | `loopback` | 同 `LdapWireOptions.BindAddress` | 同上（共 counter） | `TlsHandshakeTimeout` 30 s（取 slot 於 accept，先於 handshake） | `IdleTimeout` 5 min | `MaxMessageLength` 1 MiB |
| gRPC | 5090 | `loopback` | `GrpcWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConcurrentConnections` 100 (Kestrel) | Kestrel HTTP/2 defaults | Kestrel HTTP/2 defaults | Kestrel HTTP/2 defaults |
| LDAP / LDAPS | 389 / `LdapsPort` (nul 則無) | `loopback` | `LdapWireOptions.BindAddress` = `IPAddress.Loopback`（二 port 共此一 posture） | `MaxConnections` 100（跨二 listener 之總數） | `AuthenticationTimeout` 30 s | `IdleTimeout` 5 min | `MaxMessageLength` 1 MiB |
| HTTP (GraphQL, S3, OData, MCP-HTTP, Prometheus) | host's Kestrel | 隨 host 之 Kestrel 配置；BifrostQL 不自binds | — | host | host | host | host |

規約，凡新 `IProtocolAdapter` 必守：

- **Bind default 必 loopback。** `ListenAnyIP` 禁；用 `kestrel.Listen(options.BindAddress, …)`。`ProtocolListenerPostureTests` 釘之。
- **Admission slot 必取於 ACCEPT**，先於 read、TLS handshake、authentication。cap 若後施，僅bound admitted sessions，不bound unauthenticated peer 所能forced 之work——非 cap。用 `ProtocolConnectionLimiter`，且每 adapter 自有 subtype（共用 base type 於 DI 則二 front doors 共一 counter）。
- **Pre-auth deadline 必有。** slot 既取於 accept，silent peer 即 denial of service，無需credentials，無需bytes。authenticated 之後宜放寬或去之——idle authenticated session 乃 pooled client。
- **Per-session state 必 capped**（pgwire `MaxPreparedStatements`/`MaxPortals`）：session-lifetime 之 map 無 cap，則一 peer 於一 connection 內即可耗memory，connection cap 不救。
- **Credential 必不受於 cleartext transport。** 凡 adapter 之 handshake 以 wire 載 credential（LDAP simple bind、RESP AUTH、pgwire password 之屬），必於**讀、查、比 credential 之前**拒非 confidential connection，且 refusal 唯言 transport（勿因帳號存否而異，否則成 enumeration oracle）。development override 得存，然默 OFF、必 startup warning、且不得由「無 cert」推得。TLS 之 in-band upgrade（StartTLS 之屬）唯一 legal pre-bind state 受之；buffer 殘餘即 protocol error 斷線（pipelined plaintext 不得越 upgrade），handshake 敗即斷，無 cleartext 退路。
- **Search/read surface 必 per-request bounded，且 client 唯得narrow。** LDAP `MaxSearchResults`/`MaxSearchDuration`/`MaxMembersPerEntry` 為 server ceiling；client 之 `sizeLimit`/`timeLimit`/page size 唯narrow，永不raise。觸限必report（`sizeLimitExceeded`/`timeLimitExceeded`/`adminLimitExceeded`），不得silent truncate——似完整之partial result 較explicit partial 尤惡。join fan-out 之bound 必per-entry 施，非唯aggregate：aggregate-only 則一巨group 得riding 於諸小group 之page。
- **Continuation/paging cookie 必 MAC，且 binding 必 re-derive 於 live request。** cookie 唯carry position；scope 由 pipeline 之 tenant/policy/soft-delete 保，非由 cookie。binding（search shape、page size、identity fingerprint）入 MAC 而不transmit，故 cross-search / cross-identity replay 皆fail closed。forge、tamper、cross-context、expiry 必同一outcome。cookie 不validate 則explicit refuse，勿fallback「從頭再scan」。參 `LdapPageCookie`／`ODataContinuationToken`／`GrpcPageCursor`。
- **Untrusted input 之 regex 必 bounded**：`RegexOptions.NonBacktracking` 或 match timeout，且 timeout 必 map 為 adapter 自有 exception type（見 `.claude/rules/protocol-adapter-security.md` invariant 1）。client-supplied pattern（LIKE 等）宜以 non-regex scan 行之。

### Key Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `DbModel` | `Model/DbModel.cs` | Database schema representation (pure data) |
| `TableRelationshipOrchestrator` | `Model/Relationships/` | Strategy pattern for relationship detection |
| `GqlObjectQuery` | `QueryModel/GqlObjectQuery.cs` | Query tree → SQL generator |
| `ISqlDialect` | `QueryModel/ISqlDialect.cs` | Database-specific SQL abstraction |
| `ResolverBase` | `Resolvers/ResolverBase.cs` | Base class for all resolvers |
| `StringNormalizer` | `Utils/StringNormalizer.cs` | Centralized string normalization |
| `MetadataKeys` | `Model/MetadataKeys.cs` | Constants for metadata keys |
| `AppMetadataModel` | `AppMetadata/` | App-metadata overlay — client presentation layer (labels, forms, grids, relationships) |
| `IProtocolAdapter` | `BifrostQL.Server/ProtocolAdapter.cs` | Non-GraphQL front-door hosting contract; register via `AddProtocolAdapter<T>` |
| `BifrostMcpAdapter` | `BifrostQL.Mcp/` | MCP-server protocol adapter — DB as agent tools (schema/query/aggregate/search + opt-in writes); stdio via `AddProtocolAdapter<BifrostMcpAdapter>`, HTTP+bearer via `AddBifrostMcpHttp`/`MapBifrostMcp`; reads via `IQueryIntentExecutor`, writes via `IMutationIntentExecutor` (`EnableWrites` off by default), identity via `IBifrostAuthContextFactory` |
| `IQueryIntentExecutor` | `Resolvers/QueryIntentExecutor.cs` | Adapter read seam — programmatic `GqlObjectQuery`, transformers unskippable |
| `IMutationIntentExecutor` | `Resolvers/MutationIntentExecutor.cs` | Adapter write seam — full mutation transformer chain via `TableMutationPipeline` |
| `IBifrostAuthContextFactory` | `BifrostQL.Server/BifrostAuthContextFactory.cs` | Shared identity projection for all transport gates, fail-closed |
| `ProtocolAdapterConformanceTests` | `tests/BifrostQL.AdapterConformance/` | Derivable security-conformance kit; write adapters set `AdapterSupportsMutations` |

## Design Patterns

- **Strategy Pattern** - Relationship detection, transformers
- **Template Method** - SQL dialect base classes  
- **Base Classes** - Resolvers, transformers (reduce boilerplate)
- **Collector Pattern** - EAV configuration gathering

## Base Classes (Extend These)

### SQL Dialects

```csharp
// For dialects with LIMIT/OFFSET and || concatenation
public class MyDialect : StandardConcatDialectBase {
    public MyDialect() : base('"', "lastval()") { }
}
```

### Filter Transformers

```csharp
public class MyFilter : SingleColumnFilterTransformerBase {
    public MyFilter() : base("metadata-key", priority: 100) { }
    protected override TableFilter BuildFilter(...) { }
}
```

### Mutation Transformers

```csharp
public class MyMutation : MetadataMutationTransformerBase {
    public MyMutation() : base("metadata-key", priority: 100) { }
    protected override MutationTransformResult TransformCore(...) { }
}
```

### Resolvers

```csharp
public class MyResolver : TableResolverBase {
    public MyResolver(IDbTable table) : base(table) { }
    public override ValueTask<object?> ResolveAsync(IBifrostFieldContext ctx) { }
}
```

## Utilities (Use These)

```csharp
// Instead of ToLowerInvariant().Trim()
StringNormalizer.NormalizeType(column.DataType);
StringNormalizer.NormalizeName(tableName);

// Instead of magic strings
table.GetMetadataValue(MetadataKeys.Eav.Parent);
table.GetMetadataValue(MetadataKeys.Eav.ForeignKey);
```

## Metadata Keys

- Metadata key 名皆置 `src/BifrostQL.Core/Model/MetadataKeys.cs`。
- Core 實作查 metadata dictionary 與 module names，須用其 constants。
- 新增 metadata，須同改 `MetadataKeys`、metadata validation allow-lists、docs、tests。
- tenant isolation 與 soft-delete keys 尤須一致；關 security 與 mutation semantics。

## Module System

| Type | Interface | Base Class | Purpose |
|------|-----------|------------|---------|
| Filter | `IFilterTransformer` | `SingleColumnFilterTransformerBase` | Inject WHERE clauses |
| Mutation | `IMutationTransformer` | `MetadataMutationTransformerBase` | Transform mutations |
| Observer | `IQueryObserver` | - | Lifecycle hooks |

Priority ranges: 0-99 (security), 100-199 (data filtering), 200+ (app)

## SQL Dialects

| Dialect | Base Class | Identifiers | Concat |
|---------|------------|-------------|--------|
| SqlServer | `SqlDialectBase` | `[name]` | `+` |
| Postgres | `StandardConcatDialectBase` | `"name"` | `\|\|` |
| MySQL | `LimitOffsetDialectBase` | `` `name` `` | `CONCAT()` |
| SQLite | `StandardConcatDialectBase` | `"name"` | `\|\|` |

## GraphQL Query Builders

- 勿將 user-provided table, field, operator, type names 直插 GraphQL text。
- 用既有 query-builder validation helpers 與 schema-derived names。
- edit-db app 支援 composite primary keys。用 `examples/edit-db/src/lib/row-id.ts` 與 `examples/edit-db/src/lib/query-builder.ts` helpers；勿取巧直用 `primaryKeys[0]`。
- relationship joins 若取 first source/destination columns，即 single-column FK assumptions，非 composite-PK helpers。若擴之，須 document 且 test。

## Two Client Stacks (Architecture Decision)

- Shipped 產品鏈：`src/BifrostQL.UI/frontend` → `@standardbeagle/edit-db`。此為 data layer of record，自有 fetcher (`examples/edit-db/src/common/fetcher.ts`)、query-builder、mutation hooks。
- `@bifrostql/react` 與 `@bifrostql/app-shell` 為 experimental 平行棧，非 shipped 產品所用；`app-shell` 現無 importers。二包 README/package.json 已標 experimental status，勿誤認為 canonical client。
- `@bifrostql/types` + `@bifrostql/react` 得經 `bifrostql-npm-publish` workflow 發 npm（matched version pair；`workspace:*` 於 pack 時改寫為 concrete version）。0.x change policy：breaking change 必記 react CHANGELOG `### Breaking changes`，不得默改。
- 三 fetch-based GraphQL clients 現並存：`packages/@bifrostql/react/src/utils/graphql-client.ts`、`examples/edit-db/src/common/fetcher.ts`、`src/BifrostQL.UI/frontend/src/lib/transport.ts` 之 `HttpTransport`。此為已知重複，非 bug，勿逕自合併。
- 長期方向：統一於 `QueryTransport` 型 client — `frontend/src/lib/transport.ts` 之 `QueryTransport` interface 為 canonical shape（含 HTTP + binary transport probing）。任何新 client 或 unification 工作宜以此 interface 為目標，非以 `graphql-client.ts` 或 `fetcher.ts` 為準。
- 見「Transport」節：editor 尚未接上 `QueryTransport` 或等價 hook，故 unification 未完成，勿假設已完成。

## React Table Hook

- `packages/@bifrostql/react/src/hooks/use-bifrost-table.ts` 今為 thin orchestrator；internals 已抽入 `hooks/internal/` focused hooks（query-state、data、editing、export、a11y、responsive、virtual-scroll、selection、expansion、column-management、search）。新 cross-cutting state 入 focused internal hook，勿回填 main hook。
- 改此 hook 或其 internal hooks 仍須查 URL sync、local storage、editing、export、grouping、pagination、virtualization 之互動。

## Transport

- BifrostQL.UI header toggle 切 HTTP 與 binary transports，且**實路由** editor queries。`src/BifrostQL.UI/frontend/src/lib/transport-fetcher.ts` 之 `TransportGraphQLFetcher` 以 `QueryTransport` 造 edit-db `GraphQLFetcher` adapter，注入 `<Editor fetcher=...>`；故 editor 全數據路徑（`useSchema`、`useDataTable`、mutation hooks、stats）皆行經所選 transport。
- edit-db `Editor` 受 `fetcher?: GraphQLFetcher` prop；其諸 hook 由 `useFetcher()` context 取之，故單一注入即覆全部 query。改此縫須確保新增數據路徑仍經 `useFetcher()`，勿另建 HTTP client。
- App.tsx 依 `transportMode` + active profile 建 transport（`useMemo`，無副作用；binary socket 惰性開），並以 `key={editorKey-transportMode}` remount editor 使 toggle 即時改路由。profile `?profile=` query param 同灌 `graphqlPath` 與 `binaryPath`。

## Testing

- xUnit + NSubstitute + FluentAssertions
- SQL validation: `Microsoft.SqlServer.TransactSql.ScriptDom`
- Pattern: Arrange-Act-Assert with comments

## Anti-Patterns

❌ 勿 concatenate user input into SQL (use parameters)
❌ 勿 sync I/O in resolvers
❌ 勿 magic strings (use `MetadataKeys`)
❌ 勿 duplicate `ToLowerInvariant().Trim()` (use `StringNormalizer`)

## Quick Reference

```csharp
// Schema metadata — controls API behavior (server-side)
"dbo.users { tenant-filter: tenant_id }"
"dbo.orders { soft-delete: deleted_at }"

// App-metadata overlay — controls client presentation (SPA/RN)
// Standalone camelCase JSON, separate coexisting pipeline. Never merged
// into schema metadata. Load via AddBifrostAppMetadata, serve via
// UseBifrostAppMetadata (GET /_app-metadata). See AppMetadata/ and
// docs concepts/app-metadata-overlay.

// Filter operators
_eq, _neq, _lt, _lte, _gt, _gte, _contains, _in, _between, _null

// Register module
builder.Services.AddBifrostQL(o => o
    .AddFilterTransformer<MyFilter>()
    .AddMutationTransformer<MyMutation>());
```

## Docs Authority

- Canonical user docs 在 `docs/src/content/docs`。
- `docs-research` 為 exploratory/reference material，或 stale。勿據其摹行，必先核 source 與 canonical docs。

## Documentation

- `SKILLS.md` - Comprehensive developer guide
- `README.md` - Project overview
- Folder `README.md` files - Component-specific docs