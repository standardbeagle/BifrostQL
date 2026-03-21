# BifrostQL

**Zero-config GraphQL API for your existing database. One connection string. Full API.**

[Documentation](https://standardbeagle.github.io/BifrostQL/) | [GitHub](https://github.com/standardbeagle/BifrostQL) | [NuGet](https://www.nuget.org/packages/BifrostQL.Server)

```csharp
// Program.cs - that's it
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
var app = builder.Build();
app.UseBifrostQL();
await app.RunAsync();
```

```json
// appsettings.json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": { "Path": "/graphql", "Playground": "/graphiql" }
}
```

That's a complete GraphQL API. Every table, every column, every relationship. Queries, mutations, filtering, pagination, joins -- all generated from your schema at startup.

## What It Does

- **Reads your database schema, builds a GraphQL API.** Add a table or column, restart, and the field appears with the correct type and validation. No code generation. No mapping files.
- **Solves the N+1 problem.** BifrostQL generates one SQL query per table in the request, not one per row. All queries are sent in a single database round-trip and read back as multiple result sets. See [How BifrostQL solves N+1](#how-bifrostql-solves-n1) below.
- **Dynamic joins via `__join` fields.** Join any table to any other table directly in your GraphQL query. No configuration required for single-column key matches. Multi-column and explicit joins supported.
- **Directus-style filtering and pagination.** Filter on any field with operators like `_eq`, `_contains`, `_gt`, `_in`. Pagination via `limit`/`offset`. Sorting via enum fields.
- **Full mutation support.** Insert, update, upsert, and delete -- generated from primary key metadata. BifrostQL matches your input fields by name and does the right thing.

## Supported Databases

| Database | Status | Dialect |
|----------|--------|---------|
| SQL Server | Production | `BifrostQL.SqlServer` |
| PostgreSQL | Production | `BifrostQL.Ngsql` |
| MySQL | Production | `BifrostQL.MySql` |
| SQLite | Experimental | `BifrostQL.Sqlite` |

## Module System

Cross-cutting concerns handled via metadata configuration. No custom code required for common patterns.

**Tenant Isolation** -- Automatic WHERE clause injection per query, keyed to the authenticated user's tenant ID. Queries physically cannot return another tenant's data.

```
"dbo.orders { tenant-filter: tenant_id }"
```

**Soft Delete** -- DELETE mutations become UPDATE mutations that set a timestamp. SELECT queries automatically exclude soft-deleted rows.

```
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
```

**Audit Columns** -- `created_by`, `updated_by`, `created_on`, `updated_on` populated automatically from the authenticated user context.

```
"dbo.*.createdOn { populate: created-on; update: none; }"
"dbo.*.updatedOn { populate: updated-on; update: none; }"
```

**Visibility Control** -- Hide system tables, internal columns, or entire schemas from the API.

```
"dbo.sys*, *.__* { visibility: hidden; }"
```

All module configuration uses a CSS-like selector syntax with glob patterns. Apply rules to one table, a pattern of tables, or every table in a schema.

## Quick Start

### Install

```bash
dotnet new web -n MyBifrostApi
cd MyBifrostApi
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.SqlServer  # or BifrostQL.Ngsql, BifrostQL.MySql
```

### Configure

Add your connection string and BifrostQL settings to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "sqlserver"
  }
}
```

### Wire Up

Replace your `Program.cs`:

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
app.UseBifrostQL();
await app.RunAsync();
```

### Run

```bash
dotnet run
# GraphQL playground at http://localhost:5000/graphiql
# API endpoint at http://localhost:5000/graphql
```

### CLI Tool

BifrostQL includes a CLI tool for schema introspection, config generation, and local serving:

```bash
dotnet tool install -g BifrostQL.Tool
bifrost serve --connection "Server=localhost;Database=mydb;..."
bifrost schema --connection "..."
bifrost config generate --connection "..."
```

## Query Examples

```graphql
# Get all users with pagination
{
  users(limit: 10, offset: 0, sort: [name_asc]) {
    data {
      userId
      name
      email
    }
  }
}

# Filter with operators
{
  orders(filter: { status: { _eq: "shipped" }, total: { _gt: 100 } }) {
    data {
      orderId
      total
      status
    }
  }
}

# Dynamic join - no config needed
{
  orders {
    data {
      orderId
      __join {
        customers(filter: { customerId: { _eq: 42 } }) {
          data {
            name
          }
        }
      }
    }
  }
}

# Insert mutation
mutation {
  insert_product(data: { name: "Widget", price: 9.99 }) {
    productId
    name
    price
  }
}
```

## How BifrostQL Solves N+1

Most GraphQL servers hit the database once per field resolver. A query that fetches 50 orders with their customers triggers 1 query for orders + 50 queries for customers -- the classic N+1 problem. DataLoader batching reduces this but still requires careful implementation per relationship.

BifrostQL takes a different approach: **one query per table, regardless of row count**.

When BifrostQL receives a GraphQL request, it:

1. **Parses the full query tree** into a `GqlObjectQuery` structure before executing anything
2. **Generates one SQL statement per table** referenced in the query. Joins use correlated subqueries to scope child rows to their parents.
3. **Sends all statements in a single database round-trip** as a concatenated batch (`SELECT ...; SELECT ...; SELECT ...`)
4. **Reads all result sets** from a single `ExecuteReader` call and assembles the response in memory

For example, this query:

```graphql
{
  orders(limit: 50) {
    data {
      orderId
      total
      __join {
        customers { data { name email } }
        order_items { data { productId quantity } }
      }
    }
  }
}
```

Generates exactly **3 SQL queries** -- one for `orders`, one for `customers`, one for `order_items` -- sent as a single batch. Not 50. Not 101. Three.

| Approach | Queries for 50 orders + customers + items |
|----------|------------------------------------------|
| Naive resolvers | 1 + 50 + 50 = **101** |
| DataLoader batching | 1 + 1 + 1 = **3** (but requires manual setup per relationship) |
| BifrostQL | **3** (automatic, zero configuration) |

This holds regardless of nesting depth. A four-level deep join across orders → customers → addresses → cities still produces exactly four queries in one round-trip.

## Authentication

BifrostQL supports OAuth2/OIDC via JWT bearer tokens. When enabled, user context drives tenant isolation and audit column population.

```json
{
  "JwtSettings": {
    "Authority": "https://your-idp.com",
    "Audience": "your-api"
  },
  "BifrostQL": {
    "DisableAuth": false
  }
}
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => builder.Configuration.Bind("JwtSettings", o));

builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
var app = builder.Build();
app.UseAuthentication();
app.UseBifrostQL();
await app.RunAsync();
```

## HTTP/3 Support

BifrostQL supports HTTP/3 (QUIC) with automatic HTTP/2 and HTTP/1.1 fallback:

```json
{
  "BifrostQL": {
    "Http3": { "Enabled": true, "HttpsPort": 5001 }
  }
}
```

```csharp
builder.UseBifrostHttp3();
```

## Configuration Reference

| Setting | Description | Default |
|---------|-------------|---------|
| `ConnectionStrings:bifrost` | Database connection string | Required |
| `BifrostQL:Path` | GraphQL endpoint path | `/graphql` |
| `BifrostQL:Playground` | GraphiQL playground path | `/graphiql` |
| `BifrostQL:DisableAuth` | Disable authentication | `false` |
| `BifrostQL:Provider` | Database provider (`sqlserver`, `postgres`, `mysql`, `sqlite`) | `sqlserver` |
| `BifrostQL:Metadata` | Array of metadata configuration rules | `[]` |

### Metadata Rule Syntax

Rules use CSS-like selectors to target tables and columns:

```
"schema.table { property: value; }"     # Specific table
"schema.table.column { property: value; }"  # Specific column
"schema.* { property: value; }"          # All tables in schema
"schema.*|has(column) { property: value; }"  # Tables with a specific column
```

| Property | Values | Description |
|----------|--------|-------------|
| `tenant-filter` | column name | Enable tenant isolation on this column |
| `soft-delete` | column name | Soft-delete timestamp column |
| `soft-delete-by` | column name | Column to record who deleted |
| `delete-type` | `soft` | Mark table for soft-delete behavior |
| `populate` | `created-by`, `updated-by`, `created-on`, `updated-on`, `deleted-on`, `deleted-by` | Auto-populate from user context |
| `update` | `none` | Mark column as read-only for updates |
| `visibility` | `hidden` | Hide from GraphQL schema |
| `label` | column name | Display label column for the table |
| `auto-join` | `true`/`false` | Enable automatic join inference |
| `dynamic-joins` | `true`/`false` | Enable `__join` fields |
| `default-limit` | number | Default page size |
| `de-pluralize` | `true`/`false` | De-pluralize table names in schema |

## Solution Structure

```
BifrostQL.sln
  src/
    BifrostQL.Core/          # Core library (net8.0, net9.0, net10.0)
    BifrostQL.Server/        # ASP.NET Core middleware
    BifrostQL.Host/          # Example host application
    BifrostQL.Tool/          # CLI tool (dotnet tool)
    BifrostQL.UI/            # Desktop app (Photino)
    data/
      BifrostQL.SqlServer/   # SQL Server dialect
      BifrostQL.Ngsql/       # PostgreSQL dialect
      BifrostQL.MySql/       # MySQL dialect
      BifrostQL.Sqlite/      # SQLite dialect
  tests/
    BifrostQL.Core.Test/
    BifrostQL.Server.Test/
```

## Docker

```bash
docker build -t bifrostql .
docker run -e ConnectionStrings__bifrost="Server=host.docker.internal;..." -p 5000:80 bifrostql
```

## License

MIT

## Links

- [GitHub](https://github.com/standardbeagle/BifrostQL)
- [NuGet - BifrostQL.Core](https://www.nuget.org/packages/BifrostQL.Core)
- [NuGet - BifrostQL.Server](https://www.nuget.org/packages/BifrostQL.Server)
