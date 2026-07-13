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
      "dbo.*.updatedOn { populate: updated-on; update: none; }",
      ":root { raw-sql: disabled; generic-table: disabled; }"
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

### Grammar and reserved characters

The rule string is parsed structurally, not delimiter-agnostically:

- **`{` and `}`** delimit the property block. The block runs from the first `{`
  to the last `}`. Selectors may not contain braces, but **property values may** —
  a value like `policy-row-scope: user_id = {user_id}` is preserved verbatim,
  so `{placeholder}` expressions are legal.
- **`;`** separates one property from the next inside the block. A value cannot
  contain a literal `;`.
- **`:`** separates a property key from its value. Only the **first** `:` splits;
  the rest of the line is the value, so values may contain `:` (e.g.
  `many-to-many: Target:Junction`, `computed-sql: name:Type:expr`).
- **`,`** separates multiple selectors sharing one property block.
- **`*`** is a wildcard in selectors. Every other character in a selector is
  matched literally, including regex metacharacters (`+`, `(`, `)`, `.` within a
  name), so a table named `data(2024)` is matched as written.

Malformed rules fail fast at load: a missing brace, an empty selector, a
property with no `:`, an empty key, a duplicate key, or an unbalanced backtick
throws an `ArgumentException` naming the offending rule rather than being
silently ignored.

### Verbatim values for complex properties

A value that itself needs `;` (its only otherwise-reserved character) can be
wrapped in backticks. The interior is taken verbatim — no `;` splitting, no
trimming — so a complex value keeps its natural form:

```
"dbo.orders { computed-sql: `full:String:{first} + {last}; other:String:{a} || {b}` }"
```

Without the backticks the internal `;` would be read as a property separator and
tear the value apart. For very large expressions, keep the rule in its own JSON
file and rely on standard `IConfiguration` layering rather than inlining a huge
string.

### Repeated properties

Most properties are last-writer-wins when several rules target the same element.
Two exceptions accumulate instead, comma-joined across rules, because they carry
list values: `join` and `many-to-many`. For example, two `many-to-many` rules on
`dbo.posts` combine into a single `Roles:UserRoles, Tags:PostTags` declaration.

### Key spelling

Keys are kebab-case. A few validation keys have a legacy glued spelling still
stored internally (`minlength`, `maxlength`); the kebab forms `min-length` and
`max-length` are accepted and normalized to them, so a config can use kebab-case
consistently.

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
| `tenant-context-key` | claim key | model | User-context key for tenant ID (default: `tenant_id`) |
| `auto-filter` | `column:claim[,column:claim]` | table | Inject filters from arbitrary user-context claims |
| `auto-filter-bypass-role` | role name | model | Role that bypasses `auto-filter` rules |
| `soft-delete` | column name | table | Soft-delete timestamp column |
| `soft-delete-by` | column name | table | Column recording who deleted |
| `delete-type` | `soft` | table | Mark table for soft-delete behavior |
| `populate` | see below | column | Auto-populate from user context |
| `update` | `none` | column | Make column read-only for updates |
| `visibility` | `hidden` | table/column | Hide from GraphQL schema |
| `label` | column name | table | Display label column |
| `join` | join declaration | table/column | Declare explicit relationships |
| `many-to-many` | `TargetTable:JunctionTable` | table | Declare a many-to-many relationship (accumulates across rules) |
| `auto-join` | `true`/`false` | model/table | Enable automatic join inference |
| `foreign-joins` | `true`/`false` | model | Enable FK-based join inference |
| `dynamic-joins` | `true`/`false` | model | Emit `_join` / `_single` containers |
| `default-limit` | number | model/table | Default page size |
| `de-pluralize` | `true`/`false` | model | De-pluralize table names in schema |
| `batch-max-size` | number | table | Maximum batch mutation size |

### Optional feature metadata

| Property | Values | Applies to | Description |
|----------|--------|-----------|-------------|
| `raw-sql` | `enabled`/`disabled` | model | Expose `_rawQuery(sql:, params:, timeout:)` |
| `raw-sql-role` | role name | model | Role required for `_rawQuery` (default: `bifrost-raw-sql`) |
| `raw-sql-timeout` | seconds | model | Max raw SQL timeout |
| `raw-sql-max-rows` | number | model | Max rows returned by raw SQL |
| `generic-table` | `enabled`/`disabled` | model | Expose `_table(name:, limit:, offset:, filter:)` |
| `generic-table-role` | role name | model | Role required for `_table` (default: `bifrost-admin`) |
| `generic-table-max-rows` | number | model | Max rows returned by `_table` |
| `generic-table-allowed` | comma list | model | Allow-list for generic table names |
| `generic-table-denied` | comma list | model | Deny-list for generic table names |
| `schema-prefix` | `enabled`/`disabled` | model | Prefix GraphQL table names with schema names |
| `schema-prefix-default` | schema name | model | Schema left unprefixed when prefixing is enabled |
| `schema-prefix-format` | format string | model | Custom schema prefix format |
| `schema-display` | `flat`/`prefix`/`field` | model | Multi-schema presentation mode |
| `schema-default` | schema name | model | Default schema for field-mode presentation |
| `schema-excluded` | comma list | model | Schemas hidden from schema-field presentation |
| `schema-permissions` | rules | model | Schema-field access rules |
| `sp-include` | regex | model | Include matching stored procedures |
| `sp-exclude` | regex | model | Exclude matching stored procedures |
| `auto-detect-app` | `disabled`, `wordpress`, etc. | model | Control app-schema detection |
| `app-schema` | detector name | model | Force a specific app-schema detector |
| `detected-app` | detector name | model | Read-only detection result metadata |

### EAV, file, and storage metadata

| Property | Values | Applies to | Description |
|----------|--------|-----------|-------------|
| `eav-parent` | table name | table | Parent table for an EAV meta table |
| `eav-fk` | column name | table | FK from EAV table to parent |
| `eav-key` | column name | table | EAV attribute-name column |
| `eav-value` | column name | table | EAV attribute-value column |
| `file` | config string | column | Mark column as a file-storage column |
| `file-storage` | config string | column | Legacy file-storage marker |
| `storage` | config string | model/table/column | Storage bucket configuration |
| `max-size` | bytes | column | Max file size |
| `content-type-column` | column name | column | Column storing MIME type |
| `file-name-column` | column name | column | Column storing original filename |
| `accept` | MIME pattern | column | Accepted upload MIME types |

### Chat metadata

Declares a chat schema over user-supplied tables — exactly one conversations table
paired with exactly one messages table per model. Both tables must be published, and
must not be change-history *targets* (they may themselves record history). See
[Chat over your tables](/concepts/chat/).

| Property | Values | Applies to | Description |
|----------|--------|-----------|-------------|
| `chat-conversations` | `enabled` | table | Mark the table as the chat conversations table |
| `chat-title` | column name | table | Optional conversation title column (conversations table only) |
| `chat-messages` | `enabled` | table | Mark the table as the chat messages table (requires the full column mapping below) |
| `chat-role` | column name | table | Message role column; must be string-typed |
| `chat-content` | column name | table | Message content column; must be string-typed |
| `chat-conversation-fk` | column name | table | Column referencing the conversations table's single-column primary key (via a declared FK or `join` rule) |
| `chat-created-at` | column name | table | Message timestamp column; must be date/time-typed |

### Chat connector metadata

Exposes a table to the chat LLM as a Claude tool. A table opts in with
`chat-connector`, naming one or more connector types: `explore` (read/query),
`media` (serve an image/file column), `plan` (gated writes). Any number of tables
may be connectors. A connector table must be published (not `visibility: hidden`)
and must not be a change-history *target* (it may itself record history). See
[Chat over your tables](/concepts/chat/#chat-connectors).

| Property | Values | Applies to | Description |
|----------|--------|-----------|-------------|
| `chat-connector` | comma list of `explore`/`media`/`plan` | table | Connector types the table exposes; unknown tokens and empty values are rejected |
| `chat-media-column` | column name | table | Image/file column a `media` connector serves (required with the `media` token). The serving mode is derived from the column type: binary-typed columns serve bytes, string-typed columns serve URLs |
| `chat-media-vision` | `enabled` | table | Send the media content to the model as vision input (`media` token required) |
| `chat-media-caption` | column name | table | Optional caption/alt-text column; must be string-typed (`media` token required) |
| `chat-plan-operations` | comma list of `insert`/`update`/`delete` | table | Write allow-list for a `plan` connector (required with the `plan` token). `delete` is never implied — it must be listed explicitly. The table must have a primary key |
| `chat-tool-description` | free text | table | Optional description feeding the generated Claude tool (any connector type); present-but-empty is rejected |

Validation fails fast at model load: unknown type tokens or operations, media/plan
keys without their type token, a media column that does not exist or is neither
binary- nor string-typed, a non-string caption column, a plan connector on a
keyless table, and any unrecognized `chat-connector-*`/`chat-media-*`/`chat-plan-*`
key are all rejected before the first chat request. An `explore` connector needs no
extra columns — any published table qualifies.

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
