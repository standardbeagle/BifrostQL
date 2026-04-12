---
title: Developer Guide
description: Guide for developers working on BifrostQL - debugging, logging, and development workflows.
---

This guide covers development workflows, debugging techniques, and best practices for working with BifrostQL.

## Development Setup

### Prerequisites

- .NET 8.0 SDK or later
- Visual Studio 2022, VS Code, or JetBrains Rider
- SQL Server (LocalDB, Express, or full), PostgreSQL, or MySQL for testing

### Clone and Build

```bash
git clone https://github.com/standardbeagle/BifrostQL.git
cd BifrostQL
dotnet build
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/BifrostQL.Core.Test

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"

# Run specific test
dotnet test --filter "FullyQualifiedName~GqlObjectQueryEdgeCaseTest"
```

## VS Code Development

### Recommended Extensions

The repository includes `.vscode/extensions.json` with recommended extensions:

- **C# Dev Kit** - Full C# development support
- **.NET Test Explorer** - Run and debug tests from the sidebar
- **GitLens** - Enhanced Git integration
- **GraphQL** - Syntax highlighting for GraphQL files

### Launch Configurations

The `.vscode/launch.json` includes configurations for:

- **BifrostQL.Host** - Run the web API server
- **BifrostQL.Tool** - Debug CLI commands
- **BifrostQL.UI** - Run the desktop application
- **Run Tests** - Execute test suite

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `F5` | Start debugging (BifrostQL.Host) |
| `Ctrl+F5` | Run without debugging |
| `Ctrl+Shift+B` | Build solution |
| `Ctrl+Shift+T` | Run tests |

## CLI Tool Development

### Building the CLI Tool

```bash
dotnet build src/BifrostQL.Tool
dotnet pack src/BifrostQL.Tool --configuration Release
```

### Installing Locally for Testing

```bash
dotnet tool install --global --add-source src/BifrostQL.Tool/nupkg BifrostQL.Tool
```

### Uninstalling

```bash
dotnet tool uninstall --global BifrostQL.Tool
```

### CLI Commands Reference

| Command | Description |
|---------|-------------|
| `bifrost serve` | Start GraphQL server |
| `bifrost test` | Test database connection |
| `bifrost schema` | Print GraphQL schema |
| `bifrost doctor` | Diagnose configuration issues |
| `bifrost watch` | Watch for schema changes |
| `bifrost export [format]` | Export schema (json/sql/markdown) |
| `bifrost config-validate` | Validate metadata rules |
| `bifrost config-generate` | Auto-generate config rules |
| `bifrost init` | Create default config file |

### Debugging CLI Commands

In VS Code, use the "BifrostQL.Tool (CLI)" launch configuration. Modify the `args` array in `launch.json` to test different commands:

```json
"args": ["doctor", "--connection-string", "Server=localhost;..."]
```

## Logging and Debugging

### Log Levels

BifrostQL uses standard Microsoft.Extensions.Logging with the following event IDs:

| Event ID | Level | Description |
|----------|-------|-------------|
| 1000 | Debug | Schema loading started |
| 1001 | Info | Schema loaded successfully |
| 1002 | Error | Schema loading failed |
| 2000 | Debug | Query parsing started |
| 2001 | Debug | Query parsed |
| 2002 | Debug | SQL generated |
| 2003 | Info | Query executed |
| 2004 | Warning | Slow query detected |
| 3000 | Debug | Mutation started |
| 3001 | Info | Mutation completed |
| 4000 | Debug | Filter transformer applied |
| 4001 | Debug | Module observer notified |

### Enabling Debug Logging

**In appsettings.json:**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "BifrostQL": "Debug"
    }
  }
}
```

**In code:**

```csharp
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddConsole();
builder.Logging.AddDebug();
```

### SQL Query Logging

To log generated SQL queries, enable Debug level logging:

```csharp
// The SQL will be logged at Debug level with correlation IDs
_logger.LogSqlDetail(correlationId, sql, parameters);
```

### Correlation IDs

All operations include a correlation ID for tracing:

```csharp
var correlationId = CorrelationIdGenerator.Generate();
_logger.SchemaLoadingStarted(correlationId, connectionHash);
```

## Error Handling

### Error Codes

BifrostQL uses structured error codes for programmatic handling:

| Code | Description |
|------|-------------|
| `CONNECTION_FAILED` | Database connection error |
| `DB_LOGIN_FAILED` | Authentication failure |
| `DB_NOT_FOUND` | Database doesn't exist |
| `DB_OBJECT_NOT_FOUND` | Table/view not found |
| `DB_CONSTRAINT_VIOLATION` | Foreign key/unique constraint violation |
| `SCHEMA_ERROR` | Schema loading/parsing error |
| `QUERY_ERROR` | Query validation error |
| `INVALID_OPERATION` | Invalid operation attempted |
| `INVALID_ARGUMENT` | Invalid argument provided |
| `INTERNAL_ERROR` | Unexpected internal error |

### Using the Doctor Command

The `doctor` command diagnoses common issues:

```bash
# Check connection and configuration
bifrost doctor --connection-string "..."

# Check with config file
bifrost doctor --config ./bifrostql.json
```

This checks:
- Connection string format
- Database connectivity
- Schema access permissions
- Configuration file validity

## Schema Watching

### Watch for Changes

During development, watch for database schema changes:

```bash
# Check every 5 seconds (default)
bifrost watch --connection-string "..."

# Custom interval (10 seconds)
bifrost watch 10 --connection-string "..."
```

When changes are detected, restart your BifrostQL server to pick them up.

## Performance Debugging

### Slow Query Detection

BifrostQL automatically logs slow queries at Warning level:

```
[2004] Slow query detected: orders, 2500ms (threshold: 1000ms)
```

Configure the threshold:

```csharp
services.AddSingleton(new BifrostLoggingConfiguration
{
    SlowQueryThresholdMs = 500
});
```

### Query Timing

Enable detailed timing logs:

```csharp
_logger.QueryExecuted(correlationId, tableName, rowCount, durationMs);
```

## Testing

### Unit Tests

Located in `tests/BifrostQL.Core.Test/`:

```bash
dotnet test tests/BifrostQL.Core.Test
```

### Integration Tests

Located in `tests/BifrostQL.Integration.Test/`:

```bash
# Requires Docker for test databases
dotnet test tests/BifrostQL.Integration.Test
```

### Server Tests

Located in `tests/BifrostQL.Server.Test/`:

```bash
dotnet test tests/BifrostQL.Server.Test
```

### Writing Tests

Use the test fixtures for common setup:

```csharp
public class MyTests : IClassFixture<DbModelTestFixture>
{
    private readonly DbModelTestFixture _fixture;

    public MyTests(DbModelTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void MyTest()
    {
        var model = _fixture.Model;
        // Test code
    }
}
```

## Debugging Tips

### Common Issues

**"No database connection configured"**
- Check connection string format
- Verify database server is running
- Use `bifrost doctor` to diagnose

**"Schema not loaded"**
- Check database permissions
- Verify user can read INFORMATION_SCHEMA
- Look for schema loading errors in logs

**Slow queries**
- Check for missing indexes
- Review query execution plans
- Adjust slow query threshold for debugging

### Using the Debugger

1. Set breakpoints in resolver code
2. Use F5 to start debugging
3. Inspect the `IBifrostFieldContext` for query details
4. Check `UserContext` for authentication/tenant data

### Logging to File

```csharp
builder.Logging.AddFile("logs/bifrostql-{Date}.txt");
```

## Best Practices

### Development Workflow

1. Use `bifrost doctor` to verify setup
2. Start with `bifrost serve` for quick testing
3. Use `bifrost watch` during schema changes
4. Export schema with `bifrost export markdown` for documentation

### Code Organization

- Core logic in `BifrostQL.Core`
- Server-specific in `BifrostQL.Server`
- CLI commands in `BifrostQL.Tool`
- Database dialects in `data/` folder

### Adding New CLI Commands

1. Create class implementing `ICommand`
2. Register in `Program.cs`
3. Add to help text in `CommandRouter.cs`
4. Write tests in `tests/BifrostQL.Tool.Test/`

## Contributing

See the main [README](https://github.com/standardbeagle/BifrostQL) for contribution guidelines.
