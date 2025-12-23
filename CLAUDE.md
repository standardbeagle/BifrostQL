# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

BifrostQL is a .NET library that automatically publishes SQL databases as GraphQL APIs. Unlike other approaches, BifrostQL builds its GraphQL schema directly from the database schema - when you add a table or column, BifrostQL automatically adds the corresponding GraphQL field with the correct type and validation.

### Key Features

- **Dynamic schema generation** from SQL Server databases (PostgreSQL planned)
- **Dynamic joins** via `__join` fields added to every table
- **Automatic filtering and pagination** using Directus-style syntax
- **Generated mutations** for inserts, updates, upserts, and deletes
- **Module system** for cross-cutting concerns (tenant isolation, soft-delete, auditing)

## Build and Test Commands

```bash
# Build entire solution
dotnet build BifrostQL.sln

# Build specific project
dotnet build src/BifrostQL.Core/BifrostQL.Core.csproj

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/BifrostQL.Core.Test/BifrostQL.Core.Test.csproj

# Run single test (use full test name with dotnet 8+)
dotnet test --filter "FullyQualifiedName=GqlObjectQueryEdgeCaseTest.TestJoinWithCompositeKey"

# Run the desktop UI (requires connection string)
./bifrostui "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"

# Run the desktop UI in headless mode (server only)
./bifrostui "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True" --headless --port 5000

# Run the Host web server
dotnet run --project src/BifrostQL.Host
```

## Solution Structure

```
BifrostQL.sln
├── src/
│   ├── BifrostQL.Core/          # Core library (multi-targeted: net8.0;net9.0)
│   ├── BifrostQL.Server/        # ASP.NET Core hosting middleware
│   ├── BifrostQL.Host/          # Example console host
│   ├── BifrostQL.UI/            # Desktop app (Photino) - net9.0 only
│   └── data/
│       ├── BifrostQL.SqlServer  # SQL Server dialect implementation
│       └── BifrostQL.Ngsql      # PostgreSQL dialect (placeholder)
├── tests/
│   ├── BifrostQL.Core.Test/
│   └── BifrostQL.Server.Test/
└── examples/
    ├── edit-db/                 # React web example
    └── host-edit-db/            # Hosted web example
```

## Architecture

### Request Flow

1. **GraphQL request** arrives at `BifrostHttpMiddleware`
2. **BifrostDocumentExecutor** loads the `DbModel` and `ISchema` from `PathCache<Inputs>` (cached per endpoint path)
3. Query is parsed into `GqlObjectQuery` tree structure via `SqlVisitor`
4. **Filter transformers** (`IFilterTransformer`) are applied in priority order to inject additional filters
5. **Mutation transformers** (`IMutationTransformer`) can transform mutation operations (e.g., DELETE → UPDATE for soft-delete)
6. **Query observers** (`IQueryObserver`) are notified at lifecycle phases
7. SQL is generated via `GqlObjectQuery.AddSqlParameterized()` using `ISqlDialect`
8. **SQL execution** via `SqlExecutionManager` and `ReaderEnum`
9. Results are assembled and returned as GraphQL response

### Core Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `DbModel` | `src/BifrostQL.Core/Model/DbModel.cs` | In-memory representation of database schema, handles table linking and metadata lookup |
| `GqlObjectQuery` | `src/BifrostQL.Core/QueryModel/GqlObjectQuery.cs` | GraphQL query tree structure, generates parameterized SQL |
| `SqlVisitor` | `src/BifrostQL.Core/QueryModel/SqlVisitor.cs` | AST visitor that parses GraphQL into `GqlObjectQuery` |
| `TableFilter` | `src/BifrostQL.Core/QueryModel/TableFilter.cs` | Filter expression tree for WHERE clause generation |
| `DbSchemaBuilder` | `src/BifrostQL.Core/Schema/DbSchemaBuilder.cs` | GraphQL schema generation from `DbModel` |
| `DbTableResolver` | `src/BifrostQL.Core/Resolvers/DbTableResolver.cs` | GraphQL field resolver that delegates to `SqlExecutionManager` |
| `QueryTransformerService` | `src/BifrostQL.Core/Modules/QueryTransformerService.cs` | Orchestrates filter/mutation transformers and observers |

### Module System (Event-Driven Query Transformation)

The module system provides hooks for cross-cutting concerns:

**Filter Transformers** (`IFilterTransformer`):
- Inject additional WHERE clause filters
- Applied in priority order (lower = applied first/innermost)
- Recommended ranges: 0-99 (security/tenant), 100-199 (data filtering/soft-delete), 200+ (application)
- Built-in: `TenantFilterTransformer`, `SoftDeleteFilterTransformer`

**Mutation Transformers** (`IMutationTransformer`):
- Transform mutation operations and data
- Can change mutation type (e.g., DELETE → UPDATE)
- Built-in: `SoftDeleteMutationTransformer` (converts DELETE to UPDATE with `deleted_at` timestamp)

**Query Observers** (`IQueryObserver`):
- Lifecycle hooks for side effects (auditing, metrics)
- Phases: `Parsed`, `Transformed`, `BeforeExecute`, `AfterExecute`
- Exceptions are logged but don't abort queries

**Mutation Modules** (`IMutationModule`):
- Modify mutation data before execution
- Built-in: `BasicAuditModule` (auto-populates created/updated/deited audit columns)

### Metadata Configuration System

Behavior is configured via table/column metadata, typically set through configuration:

```
"dbo.users { tenant-filter: tenant_id }"
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
"user-id { populate: created-by }"
```

- `tenant-filter: column` - Enables tenant isolation via `TenantFilterTransformer`
- `soft-delete: column` - Enables soft-delete filtering and mutation transformation
- `populate: created-by` - Auto-populates column with user context value

### SQL Dialects

Database-specific SQL generation is abstracted through `ISqlDialect`:
- `SqlServerDialect` - SQL Server syntax (bracket identifiers, OFFSET/FETCH pagination)
- Future: PostgreSQL, MySQL, SQLite dialects

### Join System

Tables are automatically linked based on naming conventions:
- Single-column primary keys matching column names in other tables create implicit joins
- `__join` fields allow explicit multi-column joins in queries
- Joins can be nested arbitrarily deep

### Testing

- Test framework: xUnit
- Mocking: NSubstitute
- Assertions: FluentAssertions
- SQL validation: `Microsoft.SqlServer.TransactSql.ScriptDom`

### Configuration

BifrostQL is configured via `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "DisableAuth": true,
    "Path": "/graphql",
    "Playground": "/graphiql"
  }
}
```

### Authentication

OAuth2/OIDC support via JWT bearer tokens. When enabled:
- User context is built from claims via `BifrostContext`
- Tenant ID is expected in `UserContext["tenant_id"]` (configurable via `tenant-context-key` metadata)
- Audit fields are populated from `UserContext["user-audit-key"]`

### Important Notes

- The `BifrostQL.UI` project outputs a `bifrostui` binary (symlinked to repo root for convenience)
- GraphQL schema is cached per endpoint path in `PathCache<Inputs>`
- All SQL generation uses parameterized queries via `SqlParameterCollection` to prevent injection
- The module system is event-driven: transformers are applied, observers are notified
- Filter transformers throw to abort queries (e.g., missing tenant ID)
- Query observers catch and log exceptions without aborting
