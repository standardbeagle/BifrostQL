# DbModelLoader Refactoring Summary

## Overview

Refactored `DbModelLoader` to be database-agnostic by extracting schema reading logic into a separate interface (`ISchemaReader`) and integrating with the existing `IDbConnFactory` architecture.

## Changes Made

### 1. New Interface: `ISchemaReader`

**File**: `src/BifrostQL.Core/Model/ISchemaReader.cs`

```csharp
public interface ISchemaReader
{
    Task<SchemaData> ReadSchemaAsync(DbConnection connection);
}

public sealed record SchemaData(
    IDictionary<ColumnRef, List<ColumnConstraintDto>> ColumnConstraints,
    ColumnDto[] RawColumns,
    List<IDbTable> Tables
);
```

**Purpose**: Provides database-agnostic contract for reading schema metadata. Implementations provide database-specific SQL queries.

### 2. SQL Server Implementation: `SqlServerSchemaReader`

**File**: `src/BifrostQL.Core/Model/SqlServerSchemaReader.cs`

- Moved hardcoded SQL queries from `DbModelLoader` to `SqlServerSchemaReader`
- Preserved all existing INFORMATION_SCHEMA queries
- Maintained SQL Server-specific functions (COLUMNPROPERTY for identity detection)
- Returns `SchemaData` with parsed constraints, columns, and tables

### 3. Enhanced `IDbConnFactory` Interface

**File**: `src/BifrostQL.Core/Model/DbConnFactory.cs`

Added `ISchemaReader SchemaReader { get; }` property to interface.

**Updated `DbConnFactory` implementation**:
```csharp
public ISqlDialect Dialect => SqlServerDialect.Instance;
public ISchemaReader SchemaReader => new SqlServerSchemaReader();
```

### 4. Refactored `DbModelLoader`

**File**: `src/BifrostQL.Core/Model/DbModelLoader.cs`

**Key Changes**:
- Replaced `string _connStr` field with `IDbConnFactory _connFactory`
- Removed hardcoded SQL queries (moved to `SqlServerSchemaReader`)
- Removed `GetDtos` helper method (moved to `SqlServerSchemaReader`)
- Simplified `LoadAsync` to use factory and schema reader

**Backward Compatibility**:
```csharp
// Old constructor - still works!
public DbModelLoader(string connectionString, IMetadataLoader metadataLoader)
    : this(new DbConnFactory(connectionString), metadataLoader)
{
}

// New constructor - accepts factory
public DbModelLoader(IDbConnFactory connFactory, IMetadataLoader metadataLoader)
{
    _connFactory = connFactory;
    _metadataLoader = metadataLoader;
}
```

**New LoadAsync Implementation**:
```csharp
public async Task<IDbModel> LoadAsync(IDictionary<string, IDictionary<string, object?>>? additionalMetadata)
{
    await using var conn = _connFactory.GetConnection();
    await conn.OpenAsync();

    var schemaData = await _connFactory.SchemaReader.ReadSchemaAsync(conn);

    return DbModel.FromTables(
        schemaData.Tables,
        _metadataLoader,
        Array.Empty<DbStoredProcedure>(),
        Array.Empty<DbForeignKey>(),
        additionalMetadata);
}
```

### 5. New Test Suite

**File**: `tests/BifrostQL.Core.Test/Model/DbModelLoaderTests.cs`

- `LoadAsync_UsesConnectionFactoryAndSchemaReader` - Verifies factory integration
- `LoadAsync_BackwardCompatibility_UsesStringConstructor` - Ensures old constructor works
- `LoadAsync_PassesAdditionalMetadata_ToDbModel` - Validates metadata passthrough

## Architecture Benefits

### 1. Database Agnosticism

The schema reading logic is now abstracted behind `ISchemaReader`, making it easy to support additional databases:

```csharp
public class PostgresSchemaReader : ISchemaReader
{
    public async Task<SchemaData> ReadSchemaAsync(DbConnection connection)
    {
        // Use PostgreSQL information_schema or pg_catalog queries
        // ...
    }
}

public class PostgresConnFactory : IDbConnFactory
{
    public DbConnection GetConnection() => new NpgsqlConnection(_connectionString);
    public ISqlDialect Dialect => PostgresDialect.Instance;
    public ISchemaReader SchemaReader => new PostgresSchemaReader();
}
```

### 2. Backward Compatibility

All existing code continues to work without modification:

```csharp
// Existing usage - still works!
var loader = new DbModelLoader(connectionString, metadataLoader);
var model = await loader.LoadAsync();
```

**Locations using old constructor** (verified working):
- `src/BifrostQL.Server/Extensions.cs:314`
- `src/BifrostQL.Server/Extensions.cs:568`
- `src/BifrostQL.Tool/Commands/SchemaCommand.cs:34`
- `src/BifrostQL.Tool/Commands/ConfigValidateCommand.cs:48`
- `src/BifrostQL.Tool/Commands/ConfigGenerateCommand.cs:33`

### 3. Separation of Concerns

- **DbModelLoader**: Orchestrates model loading, agnostic to database
- **ISchemaReader**: Handles database-specific schema reading
- **IDbConnFactory**: Provides connections and dialect/schema reader
- **ISqlDialect**: Handles query generation (already existed)

### 4. Testability

Mock schema readers for unit testing without database dependencies:

```csharp
var mockSchemaReader = Substitute.For<ISchemaReader>();
mockSchemaReader.ReadSchemaAsync(Arg.Any<DbConnection>())
    .Returns(mockSchemaData);
```

## Future Database Support

To add PostgreSQL, MySQL, or SQLite support:

1. **Create schema reader implementation**:
   - `PostgresSchemaReader`
   - `MySqlSchemaReader`
   - `SqliteSchemaReader`

2. **Create connection factory implementation**:
   - `PostgresConnFactory`
   - `MySqlConnFactory`
   - `SqliteConnFactory`

3. **Create or reuse SQL dialect**:
   - `PostgresDialect` (already planned)
   - `MySqlDialect`
   - `SqliteDialect`

4. **Wire up in configuration**:
   ```csharp
   services.AddBifrostQL(options =>
   {
       options.UsePostgres(connectionString);
       // or
       options.UseMySql(connectionString);
       // or
       options.UseSqlite(connectionString);
   });
   ```

## Migration Path

**Immediate**: No changes required. All existing code works.

**Future**: Optionally switch to factory pattern for multi-database support:

```csharp
// Instead of:
var loader = new DbModelLoader(connectionString, metadataLoader);

// Use:
var factory = new PostgresConnFactory(connectionString);
var loader = new DbModelLoader(factory, metadataLoader);
```

## Acceptance Criteria Status

- [x] Create ISchemaReader interface
- [x] Refactor DbModelLoader to use IDbConnFactory
- [x] Extract schema SQL queries to dialect-specific implementations
- [x] Maintain backward compatibility

All acceptance criteria met. Existing code continues to work unchanged.
