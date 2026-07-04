# BifrostQL Agent Guide

AI 治 BifrostQL，宜循此約。此庫多生成面、字串驅動擴點；凡自動改作，先視此為維護圖。

## Project Overview

BifrostQL 乃 .NET 函庫，以 SQL 資料庫發布為 GraphQL APIs；由資料庫 schema 直建 GraphQL schema。

## Build & Test

```bash
dotnet build BifrostQL.sln
dotnet test
dotnet test --filter "FullyQualifiedName=TestName"
./bifrostui "connection-string"  # Desktop UI
dotnet run --project src/BifrostQL.Host  # Web server
```

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

- Shipped 產品鏈：`src/BifrostQL.UI/frontend` → `@standardbeagle/edit-db`。此為 data layer of record，自有 fetcher (`edit-db/common/fetcher.ts`)、query-builder、mutation hooks。
- `@bifrostql/react` 與 `@bifrostql/app-shell` 為 experimental 平行棧，非 shipped 產品所用；`app-shell` 現無 importers。二包 README/package.json 已標 experimental status，勿誤認為 canonical client。
- 三 fetch-based GraphQL clients 現並存：`packages/@bifrostql/react/src/utils/graphql-client.ts`、`packages/@standardbeagle/edit-db` 之 `common/fetcher.ts`、`src/BifrostQL.UI/frontend/src/lib/transport.ts` 之 `HttpTransport`。此為已知重複，非 bug，勿逕自合併。
- 長期方向：統一於 `QueryTransport` 型 client — `frontend/src/lib/transport.ts` 之 `QueryTransport` interface 為 canonical shape（含 HTTP + binary transport probing）。任何新 client 或 unification 工作宜以此 interface 為目標，非以 `graphql-client.ts` 或 `fetcher.ts` 為準。
- 見「Transport」節：editor 尚未接上 `QueryTransport` 或等價 hook，故 unification 未完成，勿假設已完成。

## React Table Hook

- `packages/@bifrostql/react/src/hooks/use-bifrost-table.ts` 為 public API compatibility 故廣。宜抽 internals 入 focused helpers/hooks，勿再增 cross-cutting state 於 main hook。
- 改此 hook 須查 URL sync、local storage、editing、export、grouping、pagination、virtualization。

## Transport

- BifrostQL.UI header 可 probe HTTP 與 binary transports；惟 `@standardbeagle/edit-db` 仍由 HTTP `uri` prop 執 editor queries。
- editor 未受 `QueryTransport` 或等價 hook 前，勿視 transport toggle 為 full editor transport routing。

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