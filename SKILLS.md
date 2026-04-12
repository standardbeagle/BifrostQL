# BifrostQL Project Skills

> AI agent guidance for working with the BifrostQL codebase.

## Project Overview

**BifrostQL** is a .NET library that automatically publishes SQL databases as GraphQL APIs. It builds GraphQL schemas directly from database schemas—when you add a table or column, BifrostQL automatically exposes it via GraphQL with correct types and validation.

### Key Capabilities

- **Dynamic schema generation** from SQL Server, PostgreSQL, MySQL, and SQLite databases
- **Zero N+1 problem**—generates one SQL query per table, not per row
- **Dynamic joins** via `__join` fields on every table
- **Directus-style filtering** (`_eq`, `_contains`, `_gt`, `_in`, etc.)
- **Automatic mutations** for insert, update, upsert, and delete
- **Module system** for cross-cutting concerns (tenant isolation, soft-delete, auditing)

---

## Architecture

### Request Flow

```
GraphQL Request → BifrostHttpMiddleware → BifrostDocumentExecutor
                                            ↓
                    DbModel + ISchema (cached per path in PathCache<Inputs>)
                                            ↓
                    SqlVisitor parses into GqlObjectQuery tree
                                            ↓
                    Filter Transformers → Mutation Transformers → Query Observers
                                            ↓
                    SQL Generation (GqlObjectQuery.AddSqlParameterized)
                                            ↓
                    SqlExecutionManager + ReaderEnum → GraphQL Response
```

### Core Components

| Component | Purpose |
|-----------|---------|
| `DbModel` | In-memory database schema representation (pure data container) |
| `TableRelationshipOrchestrator` | Orchestrates relationship detection via strategy pattern |
| `GqlObjectQuery` | GraphQL query tree structure; generates parameterized SQL |
| `SqlVisitor` | AST visitor that parses GraphQL into `GqlObjectQuery` |
| `TableFilter` | Filter expression tree for WHERE clause generation |
| `DbSchemaBuilder` | GraphQL schema generation from `DbModel` |
| `DbTableResolver` | Field resolver delegating to `SqlExecutionManager` |
| `ISqlDialect` | Database-specific SQL generation abstraction |

### Design Patterns Used

- **Strategy Pattern** - Relationship detection (`ITableRelationshipStrategy`)
- **Template Method Pattern** - SQL dialect base classes
- **Base Class Pattern** - Resolvers, transformers, collectors
- **Factory Pattern** - Type mapping, connection factories
- **Observer Pattern** - Query lifecycle hooks

---

## Directory Structure

```
BifrostQL.sln
├── src/
│   ├── BifrostQL.Core/          # Core library (net8.0, net9.0, net10.0)
│   │   ├── Forms/               # Form builder and validation
│   │   ├── Model/               # Database model classes
│   │   │   └── Relationships/   # Strategy classes for relationship detection
│   │   ├── Modules/             # Filter/mutation transformers
│   │   ├── QueryModel/          # SQL generation and dialects
│   │   ├── Resolvers/           # GraphQL field resolvers
│   │   ├── Schema/              # GraphQL schema generation
│   │   ├── Serialization/       # Data serialization helpers
│   │   └── Utils/               # Centralized utilities (StringNormalizer, etc.)
│   ├── BifrostQL.Server/        # ASP.NET Core middleware
│   ├── BifrostQL.Host/          # Example console host
│   ├── BifrostQL.Tool/          # CLI tool (dotnet tool)
│   ├── BifrostQL.UI/            # Desktop app (Photino)
│   └── data/
│       ├── BifrostQL.SqlServer/ # SQL Server dialect
│       ├── BifrostQL.Ngsql/     # PostgreSQL dialect
│       ├── BifrostQL.MySql/     # MySQL dialect
│       └── BifrostQL.Sqlite/    # SQLite dialect
├── tests/
│   ├── BifrostQL.Core.Test/     # Core library tests
│   │   ├── Unit/                # Fast, isolated unit tests
│   │   └── Integration/         # Database integration tests
│   ├── BifrostQL.Server.Test/   # Server middleware tests
│   ├── BifrostQL.Integration.Test/  # Full integration tests
│   ├── BifrostQL.UI.Tests/      # UI tests
│   └── examples/                # Example applications
```

---

## Base Classes and Utilities

### String Normalization (src/BifrostQL.Core/Utils/)

**StringNormalizer** - Centralized string normalization:
```csharp
// Use instead of ToLowerInvariant().Trim()
var normalized = StringNormalizer.NormalizeType(column.DataType);
var name = StringNormalizer.NormalizeName(tableName);
```

### Metadata Keys (src/BifrostQL.Core/Model/MetadataKeys.cs)

**MetadataKeys** - Constants for all metadata keys:
```csharp
// Use constants instead of magic strings
var parent = table.GetMetadataValue(MetadataKeys.Eav.Parent);
var fk = table.GetMetadataValue(MetadataKeys.Eav.ForeignKey);
```

### SQL Dialect Base Classes (src/BifrostQL.Core/QueryModel/)

**SqlDialectBase** - Base class for all SQL dialects:
```csharp
public sealed class MyDialect : SqlDialectBase
{
    public MyDialect() : base('"', "||", "lastval()", " RETURNING id")
    {
    }
    
    // Override only what differs from base
    public override string Pagination(...) { ... }
}
```

**Available base classes:**
- `SqlDialectBase` - Full control over identifier quotes, concat operator
- `LimitOffsetDialectBase` - For dialects using LIMIT/OFFSET pagination
- `StandardConcatDialectBase` - For dialects using `||` for string concat

### Filter Transformer Base Classes (src/BifrostQL.Core/Modules/)

**SingleColumnFilterTransformerBase** - For single-column filters:
```csharp
public sealed class MyFilterTransformer : SingleColumnFilterTransformerBase
{
    public MyFilterTransformer() : base("my-metadata-key", priority: 100)
    {
    }
    
    public override string ModuleName => "my-filter";
    
    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        return TableFilterFactory.Equals(table.DbName, columnName, value);
    }
}
```

**ContextValueFilterTransformerBase** - For filters needing user context:
```csharp
public sealed class MyContextTransformer : ContextValueFilterTransformerBase
{
    public MyContextTransformer() : base("metadata-key", "context-key", priority: 0)
    {
    }
    
    public override string ModuleName => "my-context-filter";
}
```

### Mutation Transformer Base Classes (src/BifrostQL.Core/Modules/)

**MetadataMutationTransformerBase** - For metadata-driven mutations:
```csharp
public sealed class MyMutationTransformer : MetadataMutationTransformerBase
{
    public MyMutationTransformer() : base("soft-delete", priority: 100)
    {
    }
    
    public override string ModuleName => "my-mutation";
    
    protected override MutationTransformResult TransformCore(...)
    {
        // Transform logic here
    }
}
```

**SoftDeleteMutationTransformerBase** - For soft-delete implementations:
```csharp
public sealed class MySoftDeleteTransformer : SoftDeleteMutationTransformerBase
{
    public MySoftDeleteTransformer() : base("deleted_at", priority: 100)
    {
    }
    
    public override string ModuleName => "soft-delete";
    
    protected override MutationTransformResult TransformDelete(...)
    {
        // Soft-delete logic here
    }
}
```

### Resolver Base Classes (src/BifrostQL.Core/Resolvers/)

**ResolverBase** - Abstract base for all resolvers:
```csharp
public sealed class MyResolver : ResolverBase
{
    public override ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        // Resolver logic
    }
}
```

**TableResolverBase** - For table-specific resolvers:
```csharp
public sealed class MyTableResolver : TableResolverBase
{
    public MyTableResolver(IDbTable table) : base(table)
    {
    }
    
    public override ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        var tableRef = GetTableReference(dialect);
        var whereClause = BuildWhereClause(keyValues, dialect);
        // ...
    }
}
```

**DatabaseResolverBase** - For resolvers executing SQL:
```csharp
public sealed class MyDbResolver : DatabaseResolverBase
{
    public override async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        var result = await ExecuteScalarAsync(connFactory, sql, parameters);
        return HandleDecimals(result);
    }
}
```

---

## Coding Conventions

### C# Style

- **Language version**: C# 12+ with nullable reference types enabled
- **Target frameworks**: .NET 8.0, 9.0, 10.0 (Core); .NET 9.0 only for UI
- **File-scoped namespaces**: Always use `namespace X.Y.Z;` (no braces)
- **Implicit usings**: Enabled; don't duplicate common usings

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes | PascalCase | `GqlObjectQuery` |
| Interfaces | PascalCase with I prefix | `IFilterTransformer` |
| Methods | PascalCase | `AddSqlParameterized` |
| Properties | PascalCase | `ScalarColumns` |
| Fields | camelCase with underscore | `_logger` |
| Constants | PascalCase | `DefaultLimit` |

### Code Patterns

**Prefer `init` properties for immutable data:**
```csharp
public sealed class GqlObjectQuery
{
    public IDbTable DbTable { get; init; } = null!;
    public List<GqlObjectColumn> ScalarColumns { get; init; } = new();
}
```

**Use `required` members for mandatory initialization:**
```csharp
public sealed class QueryTransformContext
{
    public required IDbModel Model { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
}
```

**Use `sealed` by default for classes:**
```csharp
public sealed class MyService { }
```

**Prefer expression-bodied members for simple operations:**
```csharp
public string KeyName => Alias ?? GraphQlName;
```

### Null Safety

- Always use nullable reference types (`string?` for nullable)
- Use null-forgiving operator (`!`) only when absolutely certain
- Prefer `is null` / `is not null` over `== null` / `!= null`

### String Comparison

- Use `StringComparison.OrdinalIgnoreCase` for case-insensitive comparisons
- Use `StringComparer.OrdinalIgnoreCase` for dictionary keys

---

## Testing Requirements

### Test Framework

- **Framework**: xUnit
- **Mocking**: NSubstitute
- **Assertions**: FluentAssertions
- **SQL validation**: `Microsoft.SqlServer.TransactSql.ScriptDom`

### Test Structure

**Unit tests follow Arrange-Act-Assert with comments:**
```csharp
[Fact]
public void AddSqlParameterized_WithSimpleFilter_GeneratesWhereClause()
{
    // Arrange
    var dbModel = StandardTestFixtures.SimpleUsers();
    var usersTable = dbModel.GetTableFromDbName("Users");
    var filter = TableFilter.FromObject(...);
    
    var query = GqlObjectQueryBuilder.Create()
        .WithDbTable(usersTable)
        .WithColumns("Id", "Name")
        .WithFilter(filter)
        .Build();

    var sqls = new Dictionary<string, ParameterizedSql>();
    var parameters = new SqlParameterCollection();

    // Act
    query.AddSqlParameterized(dbModel, Dialect, sqls, parameters);

    // Assert
    sqls.Should().ContainSingle();
    sqls["Users"].Sql.Should().Contain("WHERE");
}
```

---

## GraphQL Schema Conventions

### Type Naming

| Database Element | GraphQL Type |
|------------------|--------------|
| Table `users` | Type `users`, Query field `users` |
| Column `user_id` | Field `userId` (camelCase) |
| Primary key | Used for mutations, exposed as field |
| Foreign key | Creates single-link (parent) and multi-link (children) fields |

### Filter Operators

Standard Directus-style operators:
- `_eq`, `_neq` — equality
- `_lt`, `_lte`, `_gt`, `_gte` — comparison
- `_contains`, `_ncontains` — string contains
- `_in`, `_nin` — set membership
- `_between` — range
- `_null`, `_nnull` — null checks

### Metadata Configuration

CSS-like selector syntax for configuration:
```csharp
"dbo.users { tenant-filter: tenant_id }"
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
"*.__* { visibility: hidden; }"  // Hide internal columns
```

---

## Database Interaction Patterns

### SQL Generation

All SQL uses parameterized queries via `SqlParameterCollection`:
```csharp
var cmdText = $"SELECT {columnSql} FROM {tableRef}";
var filter = GetFilterSqlParameterized(dbModel, dialect, parameters);
var baseSql = new ParameterizedSql(cmdText, Array.Empty<SqlParameterInfo>())
    .Append(filter)
    .Append(pagination);
```

### Dialect Abstraction

Implement `ISqlDialect` for database-specific syntax, or extend base classes:
```csharp
// Simple dialect using base class
public sealed class PostgresDialect : StandardConcatDialectBase
{
    public PostgresDialect() : base('"', "lastval()", " RETURNING id AS ID")
    {
    }
}

// Complex dialect with overrides
public sealed class SqlServerDialect : SqlDialectBase
{
    public SqlServerDialect() : base("[", "]", "+", "SCOPE_IDENTITY()", " OUTPUT INSERTED.id AS ID")
    {
    }
    
    public override string Pagination(...) { /* SQL Server specific */ }
}
```

### Connection Management

- Use `IDbConnFactory` for creating connections
- Connections are short-lived (created per request)
- Use `SqlExecutionManager` for batch query execution

---

## Common Patterns

### Adding a New Filter Transformer

```csharp
public sealed class MyFilterTransformer : SingleColumnFilterTransformerBase
{
    public MyFilterTransformer() : base("my-metadata-key", priority: 100)
    {
    }
    
    public override string ModuleName => "my-filter";
    
    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        var value = context.UserContext.GetValueOrDefault("my_value");
        return TableFilterFactory.Equals(table.DbName, columnName, value);
    }
}
```

### Adding a New SQL Dialect

1. Create project in `src/data/BifrostQL.{Name}/`
2. Extend `SqlDialectBase`, `LimitOffsetDialectBase`, or `StandardConcatDialectBase`
3. Override only methods that differ from base
4. Implement `ISchemaReader`, `ITypeMapper`, `IDbConnFactory`

```csharp
public sealed class OracleDialect : SqlDialectBase
{
    public OracleDialect() : base('"', "||", "seq_name.CURRVAL")
    {
    }
    
    // Oracle uses ROWNUM for pagination
    public override string Pagination(...) { ... }
}
```

### Adding a New Resolver

```csharp
public sealed class MyResolver : TableResolverBase
{
    public MyResolver(IDbTable table) : base(table)
    {
    }
    
    public override async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
    {
        var bifrost = new BifrostContextAdapter(context);
        var tableRef = GetTableReference(bifrost.ConnFactory.Dialect);
        // ... resolver logic
    }
}
```

---

## Anti-Patterns

### Don't

- **Don't** concatenate user input into SQL—always use parameters
- **Don't** use synchronous I/O in resolvers
- **Don't** cache `DbModel` per-request—use `PathCache<Inputs>`
- **Don't** throw generic `Exception`—use `BifrostExecutionError` for GraphQL errors
- **Don't** mutate `DbModel` after initialization—it's cached
- **Don't** use magic strings for metadata keys—use `MetadataKeys` constants
- **Don't** duplicate `ToLowerInvariant().Trim()`—use `StringNormalizer`

### Do

- **Do** use `ILogger<T>` for all logging
- **Do** use `StringComparison.OrdinalIgnoreCase` for case-insensitive operations
- **Do** validate SQL syntax in tests using `ScriptDom`
- **Do** register transformers with appropriate priority values
- **Do** use `sealed` for classes not designed for inheritance
- **Do** extend base classes to reduce boilerplate
- **Do** use the Strategy pattern for complex algorithms

---

## Build and Test Commands

```bash
# Build entire solution
dotnet build BifrostQL.sln

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj

# Run single test
dotnet test --filter "FullyQualifiedName=GqlObjectQuerySqlTest.TestJoinWithCompositeKey"

# Run the desktop UI
./bifrostui "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"

# Run the Host web server
dotnet run --project src/BifrostQL.Host
```

---

## Documentation Links

- [README.md](README.md) — Project overview and quick start
- [CLAUDE.md](CLAUDE.md) — Detailed architecture and development guide
- [GitHub Repository](https://github.com/standardbeagle/BifrostQL)
- [NuGet Package](https://www.nuget.org/packages/BifrostQL.Server)
- [Full Documentation](https://standardbeagle.github.io/BifrostQL/)

---

## Quick Reference

### Adding a Column to DbModel

```csharp
new ColumnDto
{
    ColumnName = "MyColumn",
    DataType = "nvarchar",
    IsNullable = true,
    MaxLength = 255
}
```

### Creating a Table Filter

```csharp
var filter = TableFilter.FromObject(new Dictionary<string, object?>
{
    { "Status", new Dictionary<string, object?> { { "_eq", "active" } } }
}, "MyTable");
```

### Registering a Transformer

```csharp
builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration)
    .AddFilterTransformer<MyFilterTransformer>());
```

### Using StringNormalizer

```csharp
// Instead of: type?.ToLowerInvariant().Trim() ?? ""
// Use:
var normalized = StringNormalizer.NormalizeType(column.DataType);
```

### Using MetadataKeys

```csharp
// Instead of: table.GetMetadataValue("eav-parent")
// Use:
var parent = table.GetMetadataValue(MetadataKeys.Eav.Parent);
```
