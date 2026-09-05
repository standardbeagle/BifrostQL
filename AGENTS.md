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

故凡改 `src/BifrostQL.Server/`、`src/BifrostQL.Core/`、`src/BifrostQL.Abstractions/`（皆 multi-TFM）之 slice，收工前必行 `dotnet build BifrostQL.sln` 一次。epic-tier `dotnet test` 唯建 net10.0，故 net9+ 專屬之 BCL API 全綠而破 release tier（H11 之 `System.Threading.Lock` 即是）。全 solution build 數十秒，release-tests 失敗則一 rewind。

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
3. `SqlVisitor` 解析成 `GqlObjectQuery` tree —— 唯 **table root fields** 入此 tree（`SqlContext.GetFinalQueries` 以 positive table match 濾之）；其餘 root fields（introspection、`<t>Aggregate`／`Pivot`／`History`、`_rawQuery`、`_dbSchema`）由各自 resolver 擁，不經此。諸 sibling root resolvers 共一 parse task，故此 builder 內任一 throw 乃全 document 之 fault，非單 field 之 fault。
4. 套 Filter/Mutation transformers
5. SQL 由 `GqlObjectQuery.AddSqlParameterized()` 生
6. `SqlExecutionManager` 執 SQL
7. 結果返為 GraphQL response

**Resolver 若自 selection set 導 SQL work，勿讀 `IResolveFieldContext.SubFields`。** `SubFields` 以 response key（alias）為鍵，每鍵唯存一 node：故一 schema field 若受二 alias（`s1: _sum {…} s2: _sum {…}`），或一次 flat 一次經 fragment spread，則以 schema name 查之者唯得其末，餘 column 靜靜 serialise 為 null。且 `__typename` 亦列於 sub-fields 而非 column。正法：走 raw `context.FieldAst.SelectionSet`，解 inline 與 named fragment，每 schema name 存**全部** node 並 union 其 sub-fields，sub-field 名去重（重複 alias 令 result-set alias 相撞，`SqlExecutionManager` 之 `ToDictionary` 遂拋泛 DB error），`__` 前綴者跳過。Canonical walk 在 `AggregateTableResolver` 之 `SelectedFieldNodes` / `SelectedSubFieldNames` / `WalkSelections`；新 selection-derived resolver 宜引之，勿另寫。（`@skip`/`@include` 此 walk 不解，唯致 over-projection，不誤服 client。）<!-- written_at: 2026-09-04T00:00:00Z  source_event: task:01M1KNYNEFYP8M0RC70387X4SM, git:87ea68c1 -->

非 GraphQL 前門（protocol adapters）：adapter 僅擁 wire + codec。讀經 `IQueryIntentExecutor`（內delegate `SqlExecutionManager.ExecuteIntentAsync`），寫經 `IMutationIntentExecutor`（內 delegate `TableMutationPipeline`）；transformers 於彼二處套，adapter 無 API 可繞。identity 必經 `IBifrostAuthContextFactory`（諸 transport gates 共享，fail-closed）。非 HTTP 宿 Kestrel `ConnectionHandler` + `IHostedService`；contract 無 `HttpContext`。詳 docs concepts/protocol-adapters、guides/protocol-adapters。

**Keyed-write seams（五處，各自 re-derive key split）**：`TableMutationPipeline`、`BatchMutationPipeline`、`BulkBatchPlanBuilder`（set-based fast path，繞前二者）、`FilteredUpdatePipeline`、`Storage/FilePointerAccess`；`MutationIntentExecutor` 為其入口。凡涉 row-addressing／key-predicate 之 finding 或 fix，scope 必含全五處——修其一不及其餘（M3 partial-composite-key 即如是：per-row 修畢，bulk fast path 仍漏）。此重複為已知 root cause，REFACTOR task `01M1KP14XGKWY2C01TN8FDG3X7` 承之；並見 `.claude/rules/protocol-adapter-security.md` invariant 8。

### Listener Exposure Posture

每 network listener 必declare exposure 與 concrete caps。**未declare 即 `loopback`**；widening（loopback → lan → public）乃 operator 之決，非 agent 之決。以下為 shipped defaults，非 recommendation ceiling：

| Listener | Port | Posture | Bind default | Max connections | Pre-auth deadline | Idle deadline | Max message |
|----------|------|---------|--------------|-----------------|-------------------|---------------|-------------|
| pgwire | 5432 | `loopback` | `PgWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100 | `HandshakeTimeout` 30 s；SCRAM `MaxAuthAttemptsPerSource` 100 / `AuthRateLimitWindow` 1 min（key = client IP via `ProtocolSourceKey`，非 ip:port；拒先於 credential lookup，wire shape 同 invalid_password）；credential store 存 SCRAM verifiers（`PgScramVerifier.Derive`，fixed per-user salt），unknown username 之 decoy salt 必 deterministic（`Decoy(username)`）；RESP store 存 `PasswordHash` | none (authenticated session = pooled connection) | `PgProtocolIO.MaxMessageLength` 1 MiB |
| RESP | 6379 | `loopback` | `RespWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100（slot 取於 accept，先於 TLS handshake） | `AuthenticationTimeout` 30 s（`RequireAuthentication=false` 則無 pre-auth phase，逕行 idle） | `IdleTimeout` 10 min | `MaxBulkLength` 1 MiB；`MaxFrameLength` 1 MiB（一 top-level frame 之總 byte 數） |
| LDAP | 389 | `loopback` | `LdapWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConnections` 100（與 LDAPS 共此 counter） | `AuthenticationTimeout` 30 s（固定於 accept；唯 credentialed bind 撤之，anonymous session 亦止於此，anonymous rebind 不延）；`TlsHandshakeTimeout` 30 s | `IdleTimeout` 5 min（唯 credentialed session） | `MaxMessageLength` 1 MiB |
| LDAPS | `LdapsPort`（默 null＝off，慣用 636） | `loopback` | 同 `LdapWireOptions.BindAddress` | 同上（共 counter） | `TlsHandshakeTimeout` 30 s（取 slot 於 accept，先於 handshake） | `IdleTimeout` 5 min | `MaxMessageLength` 1 MiB |
| gRPC | 5090 | `loopback` | `GrpcWireOptions.BindAddress` = `IPAddress.Loopback` | `MaxConcurrentConnections` 100 (Kestrel) | Kestrel HTTP/2 defaults | Kestrel HTTP/2 defaults | Kestrel HTTP/2 defaults |
| LDAP / LDAPS | 389 / `LdapsPort` (nul 則無) | `loopback` | `LdapWireOptions.BindAddress` = `IPAddress.Loopback`（二 port 共此一 posture） | `MaxConnections` 100（跨二 listener 之總數） | `AuthenticationTimeout` 30 s（固定於 accept；anonymous session 同此 deadline） | `IdleTimeout` 5 min（唯 credentialed session） | `MaxMessageLength` 1 MiB |
| HTTP (GraphQL, S3, OData, MCP-HTTP, Prometheus) | host's Kestrel | 隨 host 之 Kestrel 配置；BifrostQL 不自binds | — | host | host | host | host |
| Binary WebSocket (`/bifrost-ws`) | host's Kestrel | 隨 host 之 Kestrel 配置；BifrostQL 不自binds | — | `UseBifrostBinary(maxConnections:)` 100（每 mount 一 counter，取 slot 於 upgrade，先於 identity gate） | `firstFrameTimeout` 30 s | `idleTimeout` 10 min | frame 4 MiB；reassembly 每 session 64 MiB、每 connection 128 MiB（皆由**received** bytes 累，非 client 所 declare） |
| LLM chat (`UseBifrostChat`, `/_chat`) | host's Kestrel | 隨 host 之 Kestrel 配置；BifrostQL 不自binds | — | host | host（identity gate 先於 body read） | host（SSE stream，一 conversation 一 stream：`409 stream-in-progress`） | 每 message `MaxMessageLength` 32768 chars（400，先於 store call）；POST body 4 × `MaxMessageLength` + 1 KiB（413 `payload-too-large`，由**received** bytes 累，declared `Content-Length` 唯早拒）；history fan-out `HistoryLimit` 50 × `MaxMessageLength` 上界每 completion 之 prompt |
| Desktop UI host (`bifrostui`：SPA + `/api/*` + `/_bridge`) | `--port`（默動態） | `loopback` | `127.0.0.1`；`--expose` 廣之於 LAN（operator 之決） | host Kestrel | 無 authentication —— 全 surface 恃 loopback binding | host Kestrel | host Kestrel |

`bifrostui` 之 HTTP bridge（`--enable-http-bridge`，`/_bridge/{kind}`）行 SQL 於 active connection 而無 authentication，故 `--expose` 與之 startup 相斥（`Program.cs` 退 2），且其 POST 必攜 `X-Bifrost-Bridge: 1` 並 `Content-Type: application/json`。

規約，凡新 `IProtocolAdapter` 必守：

- **Bind default 必 loopback。** `ListenAnyIP` 禁；用 `kestrel.Listen(options.BindAddress, …)`。`ProtocolListenerPostureTests` 釘之。
- **Admission slot 必取於 ACCEPT**，先於 read、TLS handshake、authentication。cap 若後施，僅bound admitted sessions，不bound unauthenticated peer 所能forced 之work——非 cap。用 `ProtocolConnectionLimiter`，且每 adapter 自有 subtype（共用 base type 於 DI 則二 front doors 共一 counter）。
- **Pre-auth deadline 必有，且唯 credentialed action 得退之。** slot 既取於 accept，silent peer 即 denial of service，無需credentials，無需bytes。authenticated 之後宜放寬或去之——idle authenticated session 乃 pooled client。然「retire」與「re-arm」異：deadline 若因某 free action（anonymous bind、unauthenticated ping、TLS renegotiate 之屬——即不經 rate limiter 者）而 reset，即成 renewable window，peer 逕自續命而無 credential，與無 deadline 等。詳 `.claude/rules/protocol-adapter-security.md` invariant 15。
- **Frame 必 bounded 於總 byte 數，非唯 per-element。** per-element cap（bulk length × element count）之積即一 frame 之上界；decoder 既留每 element 至 frame 畢，此積（RESP 之默認約 1 TiB）即 unauthenticated peer 可致之 retained memory。故必有 `MaxFrameLength` 之屬：per byte 扣減、payload allocate 之前檢、且唯於 top-level frame 重置（inner 重置即失其效）。
- **Credential-bearing handshake 必 rate-limited**，per-source 與 per-account 二軸（RESP `MaxAuthAttempts*`、LDAP `MaxBindAttempts*`、pgwire `MaxAuthAttemptsPerSource`）；限額之拒必先於 credential resolve，且其文不因帳號存否而異。per-source key 必取 `ProtocolSourceKey.Of(RemoteEndPoint)`（client IP），勿 `RemoteEndPoint.ToString()`——"ip:port" 之 ephemeral port 令 cap 成 per-connection，reconnect loop 永不觸限（pgwire、LDAP 各犯一次）。window/bucket 邏輯唯一在 `ProtocolAuthAttemptLimiter`，每 adapter 一 sealed subtype（`LdapBindRateLimiter`、`RespAuthRateLimiter`、`PgAuthRateLimiter`），與 `ProtocolConnectionLimiter` 同例——shared base 直註於 DI 則二 front doors 共一 budget；source-scan test（`ProtocolAuthAttemptLimiterSourceScanTests`）釘之。
- **Per-session state 必 capped**（pgwire `MaxPreparedStatements`/`MaxPortals`）：session-lifetime 之 map 無 cap，則一 peer 於一 connection 內即可耗memory，connection cap 不救。
- **Credential 必不受於 cleartext transport。** 凡 adapter 之 handshake 以 wire 載 credential（LDAP simple bind、RESP AUTH、pgwire password 之屬），必於**讀、查、比 credential 之前**拒非 confidential connection，且 refusal 唯言 transport（勿因帳號存否而異，否則成 enumeration oracle）。development override 得存，然默 OFF、必 startup warning、且不得由「無 cert」推得。TLS 之 in-band upgrade（StartTLS 之屬）唯一 legal pre-bind state 受之；buffer 殘餘即 protocol error 斷線（pipelined plaintext 不得越 upgrade），handshake 敗即斷，無 cleartext 退路。
- **Search/read surface 必 per-request bounded，且 client 唯得narrow。** LDAP `MaxSearchResults`/`MaxSearchDuration`/`MaxMembersPerEntry` 為 server ceiling；client 之 `sizeLimit`/`timeLimit`/page size 唯narrow，永不raise。觸限必report（`sizeLimitExceeded`/`timeLimitExceeded`/`adminLimitExceeded`），不得silent truncate——似完整之partial result 較explicit partial 尤惡。join fan-out 之bound 必per-entry 施，非唯aggregate：aggregate-only 則一巨group 得riding 於諸小group 之page。
- **Continuation/paging cookie 必 MAC，且 binding 必 re-derive 於 live request。** cookie 唯carry position；scope 由 pipeline 之 tenant/policy/soft-delete 保，非由 cookie。binding（search shape、page size、identity fingerprint）入 MAC 而不transmit，故 cross-search / cross-identity replay 皆fail closed。forge、tamper、cross-context、expiry 必同一outcome。cookie 不validate 則explicit refuse，勿fallback「從頭再scan」。參 `LdapPageCookie`／`ODataContinuationToken`／`GrpcPageCursor`。
- **Untrusted input 之 regex 必 bounded**：`RegexOptions.NonBacktracking` 或 match timeout，且 timeout 必 map 為 adapter 自有 exception type（見 `.claude/rules/protocol-adapter-security.md` invariant 1）。client-supplied pattern（LIKE 等）宜以 non-regex scan 行之。
- **HTTP-mounted front door（middleware，非 `IProtocolAdapter`；binary WebSocket `/bifrost-ws`、blob endpoint `UseBifrostBlobs`、與 protocol-frontend mount `UseBifrostFrontend`／`UseBifrostGraphQL` 是也）之 auth requirement 必自其所服 endpoint 導出，且 fail closed。** 導出唯一在 `MountAuthRequirement`（`src/BifrostQL.Server/MountAuthRequirement.cs`）：path-named mounts（binary、frontend）用 `Resolve`，已持 endpoint 之 GraphQL mounts 用 `ForEndpoint`；任何第二份 derivation 即 drift，source-scan test（`MountAuthRequirementSourceScanTests`）釘之。GraphQL endpoints 之 gate 置於各自 `Map` branch 內，故另路 mount 永不受其覆——mount 必自解 requirement（endpoint 不可辨、歧義、或 options 未配置皆 require auth），identity 經共享 `IBifrostAuthContextFactory` 投影，anonymous 唯 explicit（endpoint `DisableAuth` 或 mount 之 `requireAuthentication: false`）方得。requirement 必為 middleware constructor 之 **required** 參數，非 optional default：未權 posture 之 construction site 不得由省略而承 anonymous。`CreateUserContext` 乃 projection，非 gate——front door 之 identity code 若唯此一 call，即 ungated by construction。且 HTTP mounts（GraphQL、frontend、binary、chat、workflow sidecar helper）之 projection 唯一經 `BifrostIdentityGate.Project`（M13）：mount 內不得直 call `CreateUserContext`，source-scan test（`MountIdentityFunnelTests.ServerSources_NoHttpMountProjectsOutsideTheIdentityGate`）釘之；body parse 與 projection fault 皆由該 mount 之單一 funnel map 為 GraphQL-shaped error（400 malformed body、401/403 identity fault），無 exception text 上 wire；無 wire 之 helper（`GetBifrostUserContext`）於 Unprojectable 拋 `BifrostIdentityRejectedException`（constant message），**勿**退為 empty context——empty context 非 refusal（invariant 12），sidecar 之 `IsAuthenticated` gate 已放行此 principal。auth-pipeline 之二 projection（`UIAuthMiddleware` OIDC normalise、`LocalAuthEndpoint` session）同答 403，勿讓 mapper／`BuildAppIdentity` 之 fault 上 host。**且此 gate 讀 authentication middleware 所植之 principal，故 mount order 乃 load-bearing security fact**：mount 若置於 `UseBifrostQL()`／`UseBifrostEndpoints()`（即 `UseAuthentication`）之前，gate 視每 caller 為 anonymous，auth-required endpoint 遂閉全部 connection——凡 doc、sample、host wiring 之 mount order 必以此 order 示之，且改 order 之 diff 當 security diff 審。且 client-declared size 永不即 allocation：reassembly memory 必由**已收** bytes 累，declared total 唯作 bound 與 admission 之用。
- **Loopback binding 不擋 browser；故無 authentication 之 local HTTP surface 必守二事。** (a) 其 enable flag 與任一 widening flag（`--expose` 之屬）於 startup **相斥**，非 warn——binding 既為其唯一 gate，widening 即去 gate（`Program.cs` 退 non-zero）。(b) 其每一 state-changing POST 必 require 一 **custom request header**（`X-Bifrost-Bridge: 1`）：任一 origin 之 page 皆得 `fetch(..., {mode:'no-cors'})` 至 `127.0.0.1`，而 no-cors 唯不能置 custom header（其迫 preflight，此 host 不許之）。**Content-Type gate 非其代替**——no-cors 得送 `text/plain`，parser 若不校 Content-Type 即受之；二者並施，然 header 乃 load-bearing 之半（`HttpBridgeEndpoint` 之 Content-Type 檢因 `ContentLength` 為 null（chunked）而略時，header gate 仍閉之）。此 surface 之 auth requirement 不由 endpoint 導出（其無 endpoint 可導），故守此二事，非守上一 bullet 之 projection 規。<!-- written_at: 2026-09-04T02:30:00Z  source_event: task:01M1KPC4TQHK6GJWGEZX0SE08E, git:f80877bc, git:972ac962 -->
- **Row ceiling 及於每 read surface，非唯 protocol adapters。** GraphQL root reads（rows、grouped aggregate、pivot、history、nested collections）皆須經 `GqlObjectQuery.ResolveRowWindow` —— 此為唯一 ceiling 函數；`AddSqlParameterized` 之任一 branch 若於其前 return，即繞 `max-query-rows`（H7：grouped aggregate 無 LIMIT／無 ORDER BY，`groupBy: [id]` 即一 group 一 row，遂成 whole-table read）。二 corollary：(a) **unspecified limit 之 default 已折入 `ResolveRowWindow` 本身** —— 其返 **non-nullable** window（`requested ?? DefaultRowWindow`，再 clamp 至 ceiling），故 bare null 由 construction 即正，無 call site 能繞 ceiling。前身 `ClampRowLimit(IDbModel, int?)` 以 null 原樣返 null，凡 forward bare null 之 call site 即漏至下游 `ISqlDialect.Pagination`／`ConnectedPaging` 之 null→100 default（ceiling 5 之下仍得 100）——此 defect 曾四度 ship（H7、M9 二處、fe6f9129），舊 `Limit ?? DefaultRowWindow` convention 已廢。今每 row-read call site（root SELECT、restricted join-id sub-query、per-parent paged collection、grouped aggregate）唯傳 raw `Limit`；新 read site 同傳之，**勿**自行 `?? DefaultRowWindow`（重複 default 即 drift）。唯一「無 window」之合法 surface 乃 flat (non-paged) collection，其顯式傳 no-limit sentinel `-1`（`Limit ?? -1`），於 resolver 內 clamp 至 ceiling —— explicit，非 omission 之 null。(b) **read 後之 client-side `.Take(n)` 非 cap** —— 其 bound response 而不 bound database work，且自 unordered result 取 n（不 reproducible）。cap 必在 query 上，window 必有 ORDER BY，partial 必明報（sentinel：求 cap+1，過同一 clamp，`truncated` 而非可疑之 total count）；(c) **truncation flag 必載於該 surface 自身之 response envelope**，非唯 nested child summary —— 客所解者乃 envelope，故 `additionalProperties: false` 之 output schema 若未 declare 此 field，則 flag 於 wire 上結構性不可達，而 doc 之「result 內有 truncated」遂成 unverified claim（M23：declarative tool 之 envelope 曾 serialize 每 include 為 bare array）。凡 doc 言某 surface 報 partial，必核其 wire shape，非唯其 producer。auditing 一 limit guard 時，grep 其 **call site 之上的 return**，非唯其 call sites。

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
| `BifrostMcpAdapter` | `BifrostQL.Mcp/` | MCP-server protocol adapter — DB as agent tools (schema/query/aggregate/search + opt-in writes); stdio via `AddProtocolAdapter<BifrostMcpAdapter>`, HTTP+bearer via `AddBifrostMcpHttp`/`MapBifrostMcp`; reads via `IQueryIntentExecutor`, writes via `IMutationIntentExecutor` (`EnableWrites` off by default), identity via `IBifrostAuthContextFactory`. `McpAuthOptions.Mode` 默 `FailClosed` = 無 principal 即 refuse（二 transport 同）；anonymous 唯 `AnonymousDev` explicit opt-in（startup warning）。故 bare `AddProtocolAdapter<BifrostMcpAdapter>()` 拒每 call —— 諸 doc/sample snippet 必 declare mode |
| `IQueryIntentExecutor` | `Resolvers/QueryIntentExecutor.cs` | Adapter read seam — programmatic `GqlObjectQuery`, transformers unskippable |
| `IMutationIntentExecutor` | `Resolvers/MutationIntentExecutor.cs` | Adapter write seam — full mutation transformer chain via `TableMutationPipeline` |
| `IBifrostAuthContextFactory` | `BifrostQL.Server/BifrostAuthContextFactory.cs` | Shared identity projection for all transport gates, fail-closed |
| `BifrostErrorSink` | `Resolvers/BifrostErrorSink.cs` | 「sanitize the wire, keep the detail」之唯一 seam：`LookupMiss(wireMessage, detail, site)` 以 Debug 記 caller-supplied name 於 server-side，返 sanitized `BifrostExecutionError`。process-wide static（`QueryField`、`BifrostDispatcher` 所建 resolvers 無 DI），`Logger` 由 HTTP middleware／intent executors 以 `??=` 接——first writer wins。凡 lookup miss 之 sanitized throw 必經此，勿另寫 log+throw 對 |
| `ProtocolAdapterConformanceTests` | `tests/BifrostQL.AdapterConformance/` | Derivable security-conformance kit; write adapters set `AdapterSupportsMutations`. Further opt-in flags: `AdapterSupportsAuthRateLimit`, `AdapterSupportsFrameLimit`, `AdapterSupportsContinuationTokens` — each enables shared facts whose fixture hook the derivation supplies, so revert-prove per derivation |

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

Cross-cutting normalisation of mutation input (name space, casing, type coercion) belongs in `MutationTransformersWrap.TransformAsync` (`Modules/IMutationTransformer.cs`) — the one pre-chain seam every write path funnels through (single-row, batch, bulk-batch, filtered-update, file upload/delete, tree-sync). `MutationArgumentBinder` is NOT that seam: it only runs on the update/upsert key split, so insert and delete bypass it. Normalising per executor is ten sites that each have to remember, and the drift fails OPEN — a transformer whose config is written in DB column names (policy write-deny, state column, audit populate) silently never matches a payload keyed by GraphQL field name.

Observer and event emission on a write is gated on `AffectedRows > 0` — a tenant/policy-scoped-away update touches no row, so an ungated notification hands another tenant's row state to the observer chain. Gate on the affected-row count, never on the pipeline's scalar return `Value` (it is the KEY on a single-column-PK table). A read that GATES a write must carry the write's own scope: see `.claude/rules/protocol-adapter-security.md` invariant 8(d). <!-- written_at: 2026-09-04T02:15:00Z  source_event: task:01M1KPA1W14FJDK7J3JH43DSXT, git:e0b11bc7 -->

A mutation hook declares per-table applicability with `AppliesTo(IDbTable)` (`Modules/IMutationObserver.cs`); composites expose `AnyApplies(table)`. Every gate that asks "would a hook run here?" (the `updateWhere` refusal, the bulk-batch fast path) MUST ask applicability, never registration — `BifrostServiceRegistrar` registers the history, approval, deferred and CDC hooks in every host, so "is a hook registered?" is a constant `true` and any feature gated on it is dead on arrival. The default is `true` (fail-closed: an unrecognised hook keeps the per-row path), and an override MUST reuse the exact predicate the hook's own body uses to no-op, or the gate drifts from the behaviour. <!-- written_at: 2026-09-03T22:00:00Z  source_event: task:01M1KP14CKVVE0FEKMXFVWMSGF, git:eee543a9, git:e72052d9 -->

A load-time validator for a metadata key calls the SAME parse function the runtime path calls — never a second parser. `ModelConfigValidator` reaches straight into `BatchMutationPipeline.GetMaxBatchSize`, `Resolvers.BulkBatch.BulkBatchPlanBuilder.GetBulkThreshold`, `Modules.FilteredUpdateConfig.MaxAffected` and `HistoryConfig.FromTable`; a re-implemented parse drifts from the runtime one and the drift fails OPEN at request time — model load stays green while the first mutation throws. Same rule for a validator that asks a semantic question about a config (does history record update AND delete for a deferrable table): ask the parsed config object, not the raw metadata string. `ModelConfigValidator` is ~2000 lines because every key's validation lives in one file; the per-module `IConfigValidator` split is REFACTOR task `01M1KP68SF0PNS33E62V0TC78G` — put new key validation where it can move with that split. <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1KP14QNE1P5M8HBRDFXQZ5P, git:37304043, git:ff33ebdc -->

Mutation hook state has TWO scopes on `MutationObserverContext` (`Modules/IMutationObserver.cs`), and a hook must pick deliberately: `MutationState` is per ACTION (history before-image, approval divert signal, logical verb) — multi-action paths (batch, TreeSync) build a fresh one per row; `TransactionState` is per TRANSACTION (the deferred module's held change set) and is the bag shared across the batch/tree. Per-action state left in the transaction bag carries one row's decision into the next row's write.

## SQL Dialects

| Dialect | Base Class | Identifiers | Concat |
|---------|------------|-------------|--------|
| SqlServer | `SqlDialectBase` | `[name]` | `+` |
| Postgres | `StandardConcatDialectBase` | `"name"` | `\|\|` |
| MySQL | `LimitOffsetDialectBase` | `` `name` `` | `CONCAT()` |
| SQLite | `StandardConcatDialectBase` | `"name"` | `\|\|` |

## GraphQL Query Builders

- 勿將 user-provided table, field, operator, type names 直插 GraphQL text。
- 用既有 query-builder validation helpers 與 schema-derived names。generated schema type names 尤然：filter input 乃 `TableFilter<GraphQlName>Input`（唯 `query-builder.ts` 之 `tableFilterTypeName(table)` 產之），sort enum 乃 `<GraphQlName>SortEnum`；勿於 query text 內字串插值重拼——H12 即 `${name}Filter` 之誤拼，令凡 grouped grid 展開即 validation 失敗。
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
- `hooks/useTransport.ts` 依 `transportMode` + active profile 於 effect 建 transport（binary socket 惰性開），並與其 identity（`mode|graphqlPath|binaryPath`）同 publish；`editorFetcher` 唯 identity 合現選時非 null。App.tsx 以 `editorFetcher && profilesResolved` gate editor mount，故 toggle 或 profile switch 皆令 editor unmount 一 render 後 remount 於新 transport（勿為 profile 變 bump `editorKey`——H13 之根由即此；`key` 今唯載 `editorKey-transportMode-editorRouteToken`）。profile `?profile=` query param 同灌 `graphqlPath` 與 `binaryPath`。

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