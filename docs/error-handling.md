# Error Handling in BifrostQL

BifrostQL provides built-in error handling and logging capabilities that integrate seamlessly with GraphQL.NET and standard .NET logging patterns.

## Configuration

Error handling and logging can be configured in your `appsettings.json` file under the BifrostQL section:

```json
{
  "BifrostQL": {
    "Logging": {
      "EnableConsole": true,
      "EnableFile": true,
      "MinimumLevel": "Information",
      "FilePath": "logs/bifrostql.log"
    }
  }
}
```

### Configuration Options

- `EnableConsole`: (boolean, default: true) - Enables logging to console
- `EnableFile`: (boolean, default: true) - Enables logging to file
- `MinimumLevel`: (string, default: "Information") - Minimum log level to capture. Valid values are "Trace", "Debug", "Information", "Warning", "Error", "Critical"
- `FilePath`: (string, optional) - Custom log file path. If not specified, logs will be written to the default location: "logs/bifrostql-{date}.log"

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
        options.EnableConsole = true;
        options.EnableFile = true;
        options.MinimumLevel = LogLevel.Debug;
        options.LogFilePath = "custom/path/to/logs";
    }));
```

This configuration system allows for flexible error handling while maintaining consistency with GraphQL specifications and .NET practices.