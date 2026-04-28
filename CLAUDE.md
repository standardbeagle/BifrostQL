# CLAUDE.md

Concise guidance for AI tools working with BifrostQL.

## Project Overview

BifrostQL is a .NET library that publishes SQL databases as GraphQL APIs. It builds GraphQL schemas directly from database schemas.

## Build & Test

```bash
dotnet build BifrostQL.sln
dotnet test
dotnet test --filter "FullyQualifiedName=TestName"
./bifrostui "connection-string"  # Desktop UI
dotnet run --project src/BifrostQL.Host  # Web server
```

## Architecture

### Request Flow
1. GraphQL request → `BifrostHttpMiddleware`
2. `BifrostDocumentExecutor` loads cached `DbModel` + `ISchema`
3. `SqlVisitor` parses into `GqlObjectQuery` tree
4. Filter/Mutation transformers applied
5. SQL generated via `GqlObjectQuery.AddSqlParameterized()`
6. `SqlExecutionManager` executes SQL
7. Results returned as GraphQL response

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

## Testing

- xUnit + NSubstitute + FluentAssertions
- SQL validation: `Microsoft.SqlServer.TransactSql.ScriptDom`
- Pattern: Arrange-Act-Assert with comments

## Anti-Patterns

❌ Don't concatenate user input into SQL (use parameters)
❌ Don't use sync I/O in resolvers
❌ Don't use magic strings (use `MetadataKeys`)
❌ Don't duplicate `ToLowerInvariant().Trim()` (use `StringNormalizer`)

## Quick Reference

```csharp
// Metadata configuration
"dbo.users { tenant-filter: tenant_id }"
"dbo.orders { soft-delete: deleted_at }"

// Filter operators
_eq, _neq, _lt, _lte, _gt, _gte, _contains, _in, _between, _null

// Register module
builder.Services.AddBifrostQL(o => o
    .AddFilterTransformer<MyFilter>()
    .AddMutationTransformer<MyMutation>());
```

## Documentation

- `SKILLS.md` - Comprehensive developer guide
- `README.md` - Project overview
- Folder `README.md` files - Component-specific docs
