# BifrostQL

**Zero-config GraphQL API for your existing database. One connection string. Full API.**

[Documentation](https://dev.standardbeagle.com/BifrostQL/) | [GitHub](https://github.com/standardbeagle/BifrostQL) | [NuGet](https://www.nuget.org/packages/BifrostQL.Server) | [Changelog](CHANGELOG.md)

[![BifrostQL.Server](https://img.shields.io/nuget/v/BifrostQL.Server?label=BifrostQL.Server)](https://www.nuget.org/packages/BifrostQL.Server)
[![@bifrostql/react](https://img.shields.io/npm/v/@bifrostql/react?label=%40bifrostql%2Freact)](https://www.npmjs.com/package/@bifrostql/react)

```csharp
// Program.cs - that's it
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.SqlServer;

DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));

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
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx"
  },
  "BifrostQL": { "Path": "/graphql", "Playground": "/graphiql", "DisableAuth": true }
}
```

That's a complete GraphQL API. Every table, every column, every relationship. Queries, mutations, filtering, pagination, joins -- all generated from your schema at startup.

(`DisableAuth: true` is for local development. With auth on, which is the default, startup requires a `JwtSettings` section. See [Authentication](#authentication).)

## What It Does

- **Reads your database schema, builds a GraphQL API.** Add a table or column, restart, and the field appears with the correct type and validation. No code generation. No mapping files.
- **Solves the N+1 problem.** BifrostQL generates one SQL query per table in the request, not one per row. All queries are sent in a single database round-trip and read back as multiple result sets. See [How BifrostQL solves N+1](#how-bifrostql-solves-n1) below.
- **Relationship fields.** Foreign keys (composite keys included), matching key names, and many-to-many tables become nested GraphQL fields.
- **Directus-style filtering and pagination.** Filter on any field with operators like `_eq`, `_contains`, `_gt`, `_in`. Pagination via `limit`/`offset`, capped server-side by `max-query-rows`. Sorting via enum fields.
- **Full mutation support.** Insert, update, upsert, delete, batch, and graph saves -- generated from primary key metadata, each document in one transaction. BifrostQL matches your input fields by name and does the right thing.

## What Else Ships

Everything below is in the box. Each item links to its guide.

**Security and data rules**
- [Authorization policies](https://dev.standardbeagle.com/BifrostQL/guides/authorization/): row scope, column read/write deny, per-role grants
- [Multi-tenant org model](https://dev.standardbeagle.com/BifrostQL/guides/org-model/) and [tenant isolation](https://dev.standardbeagle.com/BifrostQL/guides/modules/)
- [Field encryption and masking](https://dev.standardbeagle.com/BifrostQL/concepts/field-encryption/), with [key rotation](https://dev.standardbeagle.com/BifrostQL/guides/field-encryption/)
- [Data retention and right-to-erasure](https://dev.standardbeagle.com/BifrostQL/guides/retention/)

**Write workflows**
- [Server operations](https://dev.standardbeagle.com/BifrostQL/guides/server-operations/): named, server-side C# operations with one transaction each
- [State machines](https://dev.standardbeagle.com/BifrostQL/guides/state-machines/), [workflows](https://dev.standardbeagle.com/BifrostQL/guides/workflows/), [approval workflows](https://dev.standardbeagle.com/BifrostQL/guides/approval-workflows/), [deferred effects (undo window)](https://dev.standardbeagle.com/BifrostQL/guides/deferred-effects/)
- [Change history](https://dev.standardbeagle.com/BifrostQL/guides/change-history/) and [CDC / outbound events](https://dev.standardbeagle.com/BifrostQL/guides/cdc-events/)
- [Mutation hooks](https://dev.standardbeagle.com/BifrostQL/guides/mutation-hooks/) and [extensibility](https://dev.standardbeagle.com/BifrostQL/guides/extensibility/)
- [File storage](https://dev.standardbeagle.com/BifrostQL/guides/file-storage/)

**Other front doors to the same data** ([protocol adapters](https://dev.standardbeagle.com/BifrostQL/concepts/protocol-adapters/)). Every one reads and writes through the same tenant, policy and mutation pipeline as GraphQL.
- [PostgreSQL wire protocol (pgwire)](https://dev.standardbeagle.com/BifrostQL/guides/pgwire/) for BI tools
- [Redis wire protocol (RESP)](https://dev.standardbeagle.com/BifrostQL/guides/resp/)
- [LDAP directory](https://dev.standardbeagle.com/BifrostQL/guides/ldap/)
- [gRPC](https://dev.standardbeagle.com/BifrostQL/guides/grpc/), [OData v4](https://dev.standardbeagle.com/BifrostQL/guides/odata/), [S3-compatible objects](https://dev.standardbeagle.com/BifrostQL/guides/s3/)
- [MCP server (agent tools)](https://dev.standardbeagle.com/BifrostQL/guides/mcp-server/)
- [Binary WebSocket transport](https://dev.standardbeagle.com/BifrostQL/guides/binary-transport/), [Prometheus metrics](https://dev.standardbeagle.com/BifrostQL/guides/prometheus/), [RSS/Atom feeds](https://dev.standardbeagle.com/BifrostQL/guides/feeds/)
- [LLM chat endpoints](https://dev.standardbeagle.com/BifrostQL/guides/llm-chat/) over your tables

**Clients and tools**
- [React hooks and components](https://dev.standardbeagle.com/BifrostQL/guides/react-hooks/) (`@bifrostql/react`)
- [Embeddable data editor](https://dev.standardbeagle.com/BifrostQL/guides/embedded-editor/) (`@standardbeagle/edit-db`)
- [Desktop app](https://dev.standardbeagle.com/BifrostQL/guides/desktop-app/) and [data workbench](https://dev.standardbeagle.com/BifrostQL/guides/workbench/): grids, forms, SQL editor, ERD, pivots, charts, dashboards
- [CLI tool](#cli-tool)

## Supported Databases

| Database | Status | Package | Factory |
|----------|--------|---------|---------|
| SQL Server | Production | `BifrostQL.SqlServer` | `SqlServerDbConnFactory` |
| PostgreSQL | Production | `BifrostQL.Ngsql` | `PostgresDbConnFactory` |
| MySQL / MariaDB | Production | `BifrostQL.MySql` | `MySqlDbConnFactory` |
| SQLite | Experimental | `BifrostQL.Sqlite` | `SqliteDbConnFactory` |

Dialect packages do not register themselves. Call `DbConnFactoryResolver.Register` once for each engine you use, before the host starts. See [Connect a Database](https://dev.standardbeagle.com/BifrostQL/getting-started/connect-a-database/).

## Packages

**Current release: tag `v0.4.28` = NuGet `0.4.740`, npm `@bifrostql/react` / `@bifrostql/types` `0.4.0`.**

### NuGet

| Package | Version | What it is |
|---------|---------|------------|
| `BifrostQL.Server` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Server)](https://www.nuget.org/packages/BifrostQL.Server) | ASP.NET Core hosting, auth, and every HTTP and raw-wire front door. Start here. |
| `BifrostQL.Core` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Core)](https://www.nuget.org/packages/BifrostQL.Core) | The GraphQL engine: model, schema, SQL generation, modules |
| `BifrostQL.Abstractions` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Abstractions)](https://www.nuget.org/packages/BifrostQL.Abstractions) | Contracts for module and dialect authors (metadata keys, error type, SQL primitives) |
| `BifrostQL.SqlServer` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.SqlServer)](https://www.nuget.org/packages/BifrostQL.SqlServer) | SQL Server dialect |
| `BifrostQL.Ngsql` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Ngsql)](https://www.nuget.org/packages/BifrostQL.Ngsql) | PostgreSQL dialect |
| `BifrostQL.MySql` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.MySql)](https://www.nuget.org/packages/BifrostQL.MySql) | MySQL / MariaDB dialect |
| `BifrostQL.Sqlite` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Sqlite)](https://www.nuget.org/packages/BifrostQL.Sqlite) | SQLite dialect |
| `BifrostQL.Tool` | [![nuget](https://img.shields.io/nuget/v/BifrostQL.Tool)](https://www.nuget.org/packages/BifrostQL.Tool) | `bifrost` CLI (`dotnet tool`) |

The libraries target net8.0, net9.0 and net10.0.

`BifrostQL.Mcp` (MCP server) and `BifrostQL.Aws` (S3 storage provider) are packable but **not published to NuGet yet**. Reference them from source (`src/BifrostQL.Mcp`, `src/BifrostQL.Aws`) until they are.

### npm

| Package | Version | What it is |
|---------|---------|------------|
| `@bifrostql/react` | [![npm](https://img.shields.io/npm/v/@bifrostql/react)](https://www.npmjs.com/package/@bifrostql/react) | React hooks and components |
| `@bifrostql/types` | [![npm](https://img.shields.io/npm/v/@bifrostql/types)](https://www.npmjs.com/package/@bifrostql/types) | Shared TypeScript types |
| `@standardbeagle/edit-db` | [![npm](https://img.shields.io/npm/v/@standardbeagle/edit-db)](https://www.npmjs.com/package/@standardbeagle/edit-db) | Embeddable data editor |

`@bifrostql/app-shell`, `@bifrostql/binary-client` and `@bifrostql/codegen` live in `packages/` but are not published.

### Versioning

- **NuGet versions are `0.4.<CI run number>`**, so they don't match git tag numbers: tag `v0.4.28` published `0.4.740`. `@standardbeagle/edit-db` follows the same number. `@bifrostql/react` and `@bifrostql/types` have their own semver.
- **Keep every `BifrostQL.*` package on the same version.** They are built, tested and released together from one commit, so mixing versions is untested.
- [CHANGELOG.md](CHANGELOG.md) maps each tag to its NuGet version and lists breaking changes with their migration steps. The React package keeps its own [changelog](packages/@bifrostql/react/CHANGELOG.md).

## Module System

Cross-cutting concerns handled via metadata configuration. No custom code required for common patterns.

**Tenant Isolation** -- For tables configured with `tenant-filter`, the query pipeline adds a tenant predicate from the authenticated user's context. The mutation pipeline scopes updates and deletes to that tenant and pins the tenant column on inserts and updates.

```
"dbo.orders { tenant-filter: tenant_id }"
```

**Soft Delete** -- DELETE mutations become UPDATE mutations that set a timestamp. SELECT queries automatically exclude soft-deleted rows and expose `_includeDeleted` for administrative reads.

```
"dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id }"
```

**Audit Columns** -- `created_by`, `updated_by`, `created_on`, `updated_on` populated automatically from the authenticated user context.

```
"dbo.*.createdOn { populate: created-on; }"
"dbo.*.updatedOn { populate: updated-on; }"
```

**Visibility Control** -- Hide system tables, internal columns, or entire schemas from the API.

```
"dbo.sys*, *.__* { visibility: hidden; }"
```

**Automatic Claim Filters** -- Map arbitrary user-context claims to table columns for row-level security beyond a single tenant ID.

```
"dbo.orders { auto-filter: organization_id:org_id,region_id:region }"
```

All module configuration uses a CSS-like selector syntax with glob patterns. Apply rules to one table, a pattern of tables, or every table in a schema.

## Quick Start

### Install

```bash
dotnet new web -n MyBifrostApi
cd MyBifrostApi
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.SqlServer  # or BifrostQL.Ngsql, BifrostQL.MySql, BifrostQL.Sqlite
```

### Configure

Add your connection string and BifrostQL settings to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "sqlserver"
  }
}
```

`Provider` is optional. Without it, BifrostQL works out the engine from the connection string, and fails at startup if it can't.

### Wire Up

Replace your `Program.cs`:

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.SqlServer;

// Dialect packages do not self-register. Register the provider you installed.
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
app.UseBifrostQL();
await app.RunAsync();
```

Without the `Register` call, startup fails with `No factory registered for provider …`.

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
bifrost serve --connection-string "Server=localhost;Database=mydb;..."
bifrost schema --connection-string "..."
bifrost config-generate --connection-string "..."
```

Other commands: `init`, `export` (json, sql, markdown), `config-validate`, `test`, `doctor`, `watch`. Run `bifrost help` for the full list.

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

# Relationship fields - no config needed for matching keys
{
  orders {
    data {
      orderId
      customers {
        name
      }
    }
  }
}

# Insert mutation
mutation {
  products(insert: { name: "Widget", price: 9.99 })
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
      customers {
        name
        email
      }
      order_items {
        productId
        quantity
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

Auth is **on by default**. With `DisableAuth` unset or `false`, `BindStandardConfig` refuses to start unless a `JwtSettings` section is present. BifrostQL supports OAuth2/OIDC via JWT bearer tokens, cookie-based local auth, and OIDC login. User context drives tenant isolation, policies, and audit column population.

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

**Mount order matters.** Mount the other BifrostQL front doors (`UseBifrostBinary`, `UseBifrostChat`, `MapBifrostMcp` and the rest) *after* `UseBifrostQL()` / `UseBifrostEndpoints()`. Those calls install authentication, so a front door mounted earlier sees every caller as anonymous. See the [Authentication guide](https://dev.standardbeagle.com/BifrostQL/guides/authentication/) for local auth, OIDC, and choosing which routes the login gate covers.

The MCP server refuses every call until you declare an auth mode (`McpAuthOptions.Mode`: bearer, or `AnonymousDev` for local use). See the [MCP server guide](https://dev.standardbeagle.com/BifrostQL/guides/mcp-server/).

## HTTP/3 Support

BifrostQL can configure Kestrel for HTTP/3 (QUIC) with automatic HTTP/2 and HTTP/1.1 fallback. HTTP/3 needs a TLS certificate.

```csharp
builder.UseBifrostHttp3(o => o.HttpsPort = 5001);
```

The `BifrostQL:Http3:Enabled` / `HttpsPort` / `HttpPort` settings are read by the reference host (`BifrostQL.Host`). In your own host, call `UseBifrostHttp3` yourself.

## Configuration Reference

| Setting | Description | Default |
|---------|-------------|---------|
| `ConnectionStrings:bifrost` | Database connection string | Required |
| `BifrostQL:Path` | GraphQL endpoint path | `/graphql` |
| `BifrostQL:Playground` | GraphiQL playground path | `/` |
| `BifrostQL:DisableAuth` | Disable authentication. When `false`, `JwtSettings` is required. | `false` |
| `BifrostQL:Provider` | Database provider (`sqlserver`, `postgres`, `mysql`, `sqlite`) | Detected from the connection string |
| `BifrostQL:Metadata` | Array of metadata configuration rules | `[]` |

### Metadata Rule Syntax

Rules use CSS-like selectors to target tables and columns:

```
"schema.table { property: value; }"     # Specific table
"schema.table.column { property: value; }"  # Specific column
"schema.* { property: value; }"          # All tables in schema
"schema.*|has(column) { property: value; }"  # Tables with a specific column
```

The most common properties:

| Property | Values | Description |
|----------|--------|-------------|
| `tenant-filter` | column name | Enable tenant isolation on this column |
| `tenant-context-key` | claim key | User-context key for tenant ID |
| `auto-filter` | `column:claim` list | Inject filters from user-context claims |
| `auto-filter-bypass-role` | role name | Role that bypasses auto filters |
| `soft-delete` | column name | Soft-delete timestamp column |
| `soft-delete-by` | column name | Column to record who deleted |
| `delete-type` | `soft` | Mark table for soft-delete behavior |
| `populate` | `created-by`, `updated-by`, `created-on`, `updated-on`, `deleted-on`, `deleted-by` | Auto-populate from user context |
| `visibility` | `hidden` | Hide from GraphQL schema |
| `label` | column name | Display label column for the table |
| `auto-join` | `true`/`false` | Enable automatic join inference |
| `default-limit` | number | Default page size |
| `max-query-rows` | number | Server-side row ceiling on every read (default 10000) |
| `de-pluralize` | `true`/`false` | De-pluralize table names in schema |
| `raw-sql` | `enabled`/`disabled` | Expose guarded `_rawQuery` |
| `generic-table` | `enabled`/`disabled` | Expose guarded `_table` query |
| `schema-prefix` | `enabled`/`disabled` | Prefix table names with schema names |

Policies, history, retention, encryption, state machines, events, chat, feeds and storage each have their own keys. The full list is in the [configuration reference](https://dev.standardbeagle.com/BifrostQL/reference/configuration/).

## Solution Structure

```
BifrostQL.sln
  src/
    BifrostQL.Abstractions/  # Module/dialect contracts (NuGet)
    BifrostQL.Core/          # GraphQL engine (NuGet; net8.0, net9.0, net10.0)
    BifrostQL.Server/        # ASP.NET Core hosting + protocol adapters (NuGet)
    BifrostQL.Mcp/           # MCP server adapter (not yet on NuGet)
    BifrostQL.Aws/           # AWS S3 storage provider (not yet on NuGet)
    BifrostQL.Host/          # Reference server host
    BifrostQL.Tool/          # `bifrost` CLI (dotnet tool)
    BifrostQL.UI/            # Desktop app (Photino) and workbench
    data/
      BifrostQL.SqlServer/   # SQL Server dialect
      BifrostQL.Ngsql/       # PostgreSQL dialect
      BifrostQL.MySql/       # MySQL dialect
      BifrostQL.Sqlite/      # SQLite dialect
  packages/                  # npm: @bifrostql/react, types, app-shell, binary-client, codegen
  examples/                  # edit-db (@standardbeagle/edit-db), host-edit-db, chat, forms-sample
  samples/HostedSpa/         # SPA + API hosting sample
  templates/                 # dotnet new template
  tests/
    BifrostQL.Core.Test/
    BifrostQL.Server.Test/
    BifrostQL.Integration.Test/
    BifrostQL.AdapterConformance/  # Security-conformance kit for protocol adapters
    BifrostQL.Mcp.Test/
    BifrostQL.PublicApi.Test/
    BifrostQL.Host.Test/
    BifrostQL.Tool.Test/
    BifrostQL.UI.Tests/
    BifrostQL.UI.E2E/        # Playwright
    BifrostQL.Testing/       # Shared test helpers
```

## Docker

The image runs the reference host (`BifrostQL.Host`), which registers every dialect.

```bash
docker build -t bifrostql .
docker run \
  -e ConnectionStrings__bifrost="Server=host.docker.internal;..." \
  -e BifrostQL__DisableAuth=true \
  -p 5000:8080 bifrostql
# API at http://localhost:5000/graphql, playground at http://localhost:5000/edit
```

The .NET 8+ base images listen on port 8080. Drop `BifrostQL__DisableAuth` and pass `JwtSettings__Authority` / `JwtSettings__Audience` to run with auth.

## License

MIT

## Links

- [Documentation](https://dev.standardbeagle.com/BifrostQL/)
- [Changelog](CHANGELOG.md)
- [GitHub](https://github.com/standardbeagle/BifrostQL)
- [NuGet - BifrostQL.Server](https://www.nuget.org/packages/BifrostQL.Server)
- [npm - @bifrostql/react](https://www.npmjs.com/package/@bifrostql/react)
