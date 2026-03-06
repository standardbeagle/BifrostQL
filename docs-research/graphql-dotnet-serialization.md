# JSON Serialization in GraphQL .NET

This document covers the JSON serialization and deserialization options available in GraphQL .NET.

## Overview

While JSON is not mandatory for GraphQL request/response formats, it is commonly used. GraphQL .NET provides two libraries to help with JSON serialization and deserialization of GraphQL requests and responses:

1. `GraphQL.SystemTextJson` - For use with the `System.Text.Json` library
2. `GraphQL.NewtonsoftJson` - For use with the `Newtonsoft.Json` library

## Available Libraries

### GraphQL.SystemTextJson

- Works with the `System.Text.Json` library
- Provides both synchronous and asynchronous serialization methods
- Recommended for new projects, especially those using ASP.NET Core

### GraphQL.NewtonsoftJson

- Works with the `Newtonsoft.Json` library
- Only provides synchronous serialization methods
- May be preferred for projects with existing Newtonsoft.Json dependencies

## Key Differences

The main differences between the two serialization engines are:

1. **Async Support**
   - `GraphQL.NewtonsoftJson` does not provide asynchronous serialization/deserialization methods
   - The `GraphQL.NewtonsoftJson` serialization helper performs synchronous calls on the underlying stream when writing JSON output
   - This is particularly important when hosting in ASP.NET Core, as it requires deliberate configuration to allow synchronous reading/writing of the underlying stream

2. **Performance**
   - `System.Text.Json` generally offers better performance as it's more modern and optimized
   - `Newtonsoft.Json` may be more flexible with complex serialization scenarios due to its mature feature set

## Best Practices

1. For new projects, especially those using ASP.NET Core, prefer `GraphQL.SystemTextJson`
2. If your project has existing Newtonsoft.Json dependencies or requires specific Newtonsoft.Json features, use `GraphQL.NewtonsoftJson`
3. When using `GraphQL.NewtonsoftJson` with ASP.NET Core, ensure your configuration properly handles synchronous I/O operations

## Example Usage

```csharp
// Using System.Text.Json
services.AddGraphQL(b => b
    .AddSystemTextJson());

// Using Newtonsoft.Json
services.AddGraphQL(b => b
    .AddNewtonsoftJson());
```

Remember to configure your ASP.NET Core application appropriately when using `GraphQL.NewtonsoftJson`:

```csharp
// Allow synchronous I/O when using Newtonsoft.Json
services.Configure<KestrelServerOptions>(options =>
{
    options.AllowSynchronousIO = true;
});