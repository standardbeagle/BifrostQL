# Error Handling in BifrostQL

BifrostQL provides built-in error handling and logging capabilities that integrate seamlessly with GraphQL.NET and standard .NET logging patterns.

## Configuration

Error handling and logging can be configured in your `appsettings.json` file under the BifrostQL section:

```json
{
  "BifrostQL": {
    "Logging": {
      "EnableQueryLogging": true,
      "SlowQueryThresholdMs": 1000,
      "LogSql": false
    }
  }
}
```

### Configuration Options

- `EnableQueryLogging`: (boolean, default: true) - Logs each executed query
- `SlowQueryThresholdMs`: (int, default: 1000) - Queries slower than this log a warning
- `LogSql`: (boolean, default: false) - Includes the SQL text in query logs, the slow-query warning included

Sinks (console, file) and minimum levels come from the host's standard `Logging` configuration and logging providers, not from this section.

## Error Types

BifrostQL handles three main categories of GraphQL errors:

1. **Schema Errors**: Errors that occur during schema definition or initialization
2. **Input Errors**: Validation errors, syntax errors, or other issues with the GraphQL query
3. **Processing Errors**: Runtime errors that occur during query execution

### Error Response Format

All errors are returned in the standard GraphQL error format:

```json
{
  "errors": [
    {
      "message": "Error message",
      "locations": [
        {
          "line": 2,
          "column": 3
        }
      ],
      "path": ["field", "subfield"],
      "extensions": {
        "code": "ERROR_CODE",
        "timestamp": "2025-01-28T13:37:00Z",
        "details": {}
      }
    }
  ]
}
```

## Logging

BifrostQL automatically logs all errors with appropriate context and stack traces when configured. Different error types are logged at different levels:

- Validation/Syntax Errors: `Warning`
- Authorization Errors: `Warning`
- Unhandled Exceptions: `Error`
- Schema Errors: `Error`

### Log Output Example

```
2025-01-28 13:37:00.123 [Error] GraphQL Error: Unable to resolve field 'product'
Path: query/product
Location: Line 3, Column 5
Details: Invalid product ID provided
Stack Trace: ...
```

## Custom Error Handling

For custom error handling needs, you can implement your own error handling logic by extending the built-in error handling infrastructure. Here's an example:

```csharp
services.AddGraphQL(b => b
    .AddBifrostErrorLogging(options => 
    {
        options.EnableQueryLogging = true;
        options.SlowQueryThresholdMs = 500;
        options.LogSql = true;
    }));
```

This configuration system allows for flexible error handling while maintaining consistency with GraphQL specifications and .NET practices.