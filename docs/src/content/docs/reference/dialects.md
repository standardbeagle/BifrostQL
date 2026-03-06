---
title: SQL Dialects
description: Database-specific behavior across SQL Server, PostgreSQL, MySQL, and SQLite.
---

BifrostQL abstracts database-specific SQL generation through the `ISqlDialect` interface. Each dialect handles identifier quoting, pagination syntax, type mapping, and upsert strategies for its target database.

## Supported databases

| Database | Package | Provider key | Status |
|----------|---------|-------------|--------|
| SQL Server | `BifrostQL.SqlServer` | `sqlserver` | Production |
| PostgreSQL | `BifrostQL.Ngsql` | `postgres` | Production |
| MySQL | `BifrostQL.MySql` | `mysql` | Production |
| SQLite | `BifrostQL.Sqlite` | `sqlite` | Experimental |

Set the provider in configuration:

```json
{
  "BifrostQL": {
    "Provider": "postgres"
  }
}
```

## Dialect differences

### Identifier quoting

| Database | Style | Example |
|----------|-------|---------|
| SQL Server | Brackets | `[orders].[orderId]` |
| PostgreSQL | Double quotes | `"orders"."orderId"` |
| MySQL | Backticks | `` `orders`.`orderId` `` |
| SQLite | Double quotes | `"orders"."orderId"` |

### Pagination

| Database | Syntax |
|----------|--------|
| SQL Server | `OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY` |
| PostgreSQL | `LIMIT @limit OFFSET @offset` |
| MySQL | `LIMIT @limit OFFSET @offset` |
| SQLite | `LIMIT @limit OFFSET @offset` |

SQL Server requires an `ORDER BY` clause for `OFFSET`/`FETCH` pagination. BifrostQL adds a default order by primary key when no sort is specified.

### Upsert strategy

| Database | Implementation |
|----------|---------------|
| SQL Server | `MERGE ... WHEN MATCHED THEN UPDATE WHEN NOT MATCHED THEN INSERT` |
| PostgreSQL | `INSERT ... ON CONFLICT (pk) DO UPDATE SET ...` |
| MySQL | `INSERT ... ON DUPLICATE KEY UPDATE ...` |
| SQLite | `INSERT OR REPLACE INTO ...` |

### Type mapping specifics

**SQL Server**:
- `money`, `smallmoney` map to `Decimal`
- `uniqueidentifier` maps to `String`
- `nvarchar`, `nchar`, `ntext` map to `String`

**PostgreSQL**:
- `serial`, `bigserial` are treated as auto-increment
- `uuid` maps to `String`
- `jsonb`, `json` map to `String`
- `text[]`, `integer[]` map to `[String]`, `[Int]`

**MySQL**:
- `tinyint(1)` maps to `Boolean` (MySqlConnector returns `bool` for this type)
- `enum` columns map to `String`
- `json` maps to `String`
- Column names are case-insensitive

**SQLite**:
- Type affinity rules apply: `INTEGER`, `REAL`, `TEXT`, `BLOB`
- No native `BOOLEAN` -- uses `INTEGER` with 0/1
- No native `DATETIME` -- stored as text in ISO 8601 format

## Installing a dialect

Add the dialect package alongside the core server package:

```bash
# SQL Server
dotnet add package BifrostQL.SqlServer

# PostgreSQL
dotnet add package BifrostQL.Ngsql

# MySQL
dotnet add package BifrostQL.MySql

# SQLite
dotnet add package BifrostQL.Sqlite
```

The dialect is selected at startup based on the `Provider` configuration value. Each dialect registers its own type mapper and SQL generator with the BifrostQL pipeline.

## Writing a custom dialect

Implement the `ISqlDialect` interface to add support for a new database. The dialect must provide:

- Identifier quoting
- Parameter placeholder style
- Pagination SQL generation
- Upsert SQL generation
- Type mapper (SQL types to GraphQL types)
- Schema reader (database metadata to `DbModel`)

Register the dialect in your service configuration before calling `AddBifrostQL`.
