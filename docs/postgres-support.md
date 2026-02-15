# PostgreSQL Support

## Overview

BifrostQL now supports PostgreSQL databases through the `BifrostQL.Ngsql` package, leveraging the refactored `ISchemaReader` interface.

## Installation

```bash
dotnet add package BifrostQL.Ngsql
```

## Usage

### Basic Configuration

```csharp
using BifrostQL.Ngsql;
using BifrostQL.Model;

var connectionString = "Host=localhost;Database=mydb;Username=postgres;Password=secret";
var factory = new PostgresDbConnFactory(connectionString);
var metadataLoader = new MetadataLoader(/* configuration */);
var loader = new DbModelLoader(factory, metadataLoader);

var model = await loader.LoadAsync();
```

### ASP.NET Core Integration

```csharp
services.AddBifrostQL(options =>
{
    var factory = new PostgresDbConnFactory(Configuration.GetConnectionString("Postgres"));
    options.ConnectionFactory = factory;
});
```

## Implementation Details

### PostgreSQL Dialect

The `PostgresDialect` class provides PostgreSQL-specific SQL generation:

- **Identifiers**: Double quotes (`"table_name"`)
- **Pagination**: `LIMIT/OFFSET` syntax
- **Parameters**: `@` prefix (Npgsql compatibility)
- **LIKE operations**: Case-insensitive `ILIKE`
- **Last inserted identity**: `lastval()` function

### Schema Reader

The `PostgresSchemaReader` uses `information_schema` views to read metadata:

- Filters out system schemas (`pg_catalog`, `information_schema`)
- Detects identity columns via `nextval()` in `column_default`
- Supports all standard INFORMATION_SCHEMA columns

### Differences from SQL Server

| Feature | SQL Server | PostgreSQL |
|---------|------------|------------|
| Identifier quoting | `[table]` | `"table"` |
| Pagination | `OFFSET/FETCH` | `LIMIT/OFFSET` |
| Parameter prefix | `@` | `@` (Npgsql) |
| Identity detection | `COLUMNPROPERTY()` | `nextval()` check |
| LIKE (case-insensitive) | `LIKE` | `ILIKE` |
| Last inserted ID | `SCOPE_IDENTITY()` | `lastval()` |

## Schema Compatibility

PostgreSQL schemas map to GraphQL types as follows:

| PostgreSQL Type | GraphQL Type |
|-----------------|--------------|
| `integer`, `bigint`, `smallint` | `Int` |
| `numeric`, `decimal`, `real`, `double precision` | `Float` |
| `boolean` | `Boolean` |
| `text`, `varchar`, `char` | `String` |
| `timestamp`, `timestamptz`, `date` | `DateTime` |
| `uuid` | `String` |
| `json`, `jsonb` | `String` |

## Limitations

- **Stored procedures**: Not yet supported (future enhancement)
- **Foreign keys**: Detected via `information_schema` only
- **Composite types**: Not supported
- **Arrays**: Mapped to JSON strings
- **Enums**: Mapped as strings

## Testing

The PostgreSQL implementation shares the same test suite as SQL Server, ensuring compatibility.

## Future Enhancements

- [ ] Stored procedure support via `pg_catalog.pg_proc`
- [ ] Better composite type handling
- [ ] Native array support
- [ ] Enum type mapping to GraphQL enums
- [ ] Performance optimizations for large schemas
