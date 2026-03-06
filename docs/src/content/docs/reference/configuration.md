---
title: Configuration
description: Complete settings reference for BifrostQL.
---

BifrostQL is configured through `appsettings.json` (or any ASP.NET Core configuration source). All settings live under the `BifrostQL` key.

## Full example

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": false,
    "Provider": "sqlserver",
    "Metadata": [
      "dbo.sys* { visibility: hidden; }",
      "dbo.*|has(tenant_id) { tenant-filter: tenant_id; }",
      "dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id; delete-type: soft; }",
      "dbo.*.createdOn { populate: created-on; update: none; }",
      "dbo.*.updatedOn { populate: updated-on; update: none; }"
    ]
  },
  "JwtSettings": {
    "Authority": "https://your-idp.com",
    "Audience": "your-api"
  }
}
```

## Settings reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `ConnectionStrings:bifrost` | string | *required* | Database connection string |
| `BifrostQL:Path` | string | `/graphql` | GraphQL endpoint path |
| `BifrostQL:Playground` | string | `/graphiql` | GraphiQL playground path |
| `BifrostQL:DisableAuth` | bool | `false` | Disable authentication checks |
| `BifrostQL:Provider` | string | `sqlserver` | Database provider: `sqlserver`, `postgres`, `mysql`, `sqlite` |
| `BifrostQL:Metadata` | string[] | `[]` | Array of metadata configuration rules |
| `BifrostQL:Http3:Enabled` | bool | `false` | Enable HTTP/3 (QUIC) support |
| `BifrostQL:Http3:HttpsPort` | int | `5001` | HTTPS port for HTTP/3 |

## Metadata rule syntax

Metadata rules use a CSS-like selector syntax to target tables and columns. Each rule has a selector and a block of properties:

```
"selector { property: value; property: value; }"
```

### Selectors

| Pattern | Matches |
|---------|---------|
| `dbo.orders` | The `orders` table in the `dbo` schema |
| `dbo.orders.total` | The `total` column on `dbo.orders` |
| `dbo.*` | All tables in the `dbo` schema |
| `dbo.*.createdOn` | The `createdOn` column on every table in `dbo` |
| `*.*` | All tables in all schemas |
| `dbo.sys*` | Tables starting with `sys` in `dbo` |
| `dbo.*.__*` | Columns starting with `__` on all `dbo` tables |
| `dbo.*\|has(tenant_id)` | Tables in `dbo` that have a `tenant_id` column |

### Properties

| Property | Values | Applies to | Description |
|----------|--------|-----------|-------------|
| `tenant-filter` | column name | table | Enable tenant isolation on this column |
| `tenant-context-key` | claim key | table | JWT claim key for tenant ID (default: `tenant_id`) |
| `soft-delete` | column name | table | Soft-delete timestamp column |
| `soft-delete-by` | column name | table | Column recording who deleted |
| `delete-type` | `soft` | table | Mark table for soft-delete behavior |
| `populate` | see below | column | Auto-populate from user context |
| `update` | `none` | column | Make column read-only for updates |
| `visibility` | `hidden` | table/column | Hide from GraphQL schema |
| `label` | column name | table | Display label column |
| `auto-join` | `true`/`false` | table | Enable automatic join inference |
| `dynamic-joins` | `true`/`false` | table | Enable `__join` fields |
| `default-limit` | number | table | Default page size |
| `de-pluralize` | `true`/`false` | table | De-pluralize table name in schema |

### Populate values

| Value | Description |
|-------|-------------|
| `created-by` | User audit key (on insert only) |
| `updated-by` | User audit key (on insert and update) |
| `created-on` | Current timestamp (on insert only) |
| `updated-on` | Current timestamp (on insert and update) |
| `deleted-on` | Current timestamp (on soft-delete) |
| `deleted-by` | User audit key (on soft-delete) |

### Rule ordering

Rules are applied in order. Later rules override earlier ones for the same target. Use broad rules first, then specific overrides:

```json
{
  "Metadata": [
    "dbo.* { de-pluralize: true; default-limit: 50; }",
    "dbo.audit_log { de-pluralize: false; default-limit: 100; }"
  ]
}
```

## Connection string formats

### SQL Server

```
Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True
```

### PostgreSQL

```
Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=xxx
```

### MySQL

```
Server=localhost;Port=3306;Database=mydb;User=root;Password=xxx
```

### SQLite

```
Data Source=path/to/database.db
```
