# GraphQL.NET Metrics

BifrostQL can leverage GraphQL.NET's metrics capabilities to monitor query execution performance. This document outlines how metrics are implemented and used.

## Overview

Metrics collection in GraphQL.NET provides:
- Field-level execution timing
- Parsing and validation timing
- Apollo Tracing compatibility
- Conditional enabling via HTTP headers

## Implementation

### Basic Setup

Metrics are disabled by default for performance reasons. They can be enabled in two ways:

1. Per-execution basis:
```csharp
var executor = new DocumentExecutor();
ExecutionResult result = executor.ExecuteAsync(_ =>
{
    _.Schema = schema;
    _.Query = "...";
    _.EnableMetrics = true;
    _.FieldMiddleware.Use<InstrumentFieldsMiddleware>();
});
```

2. Via dependency injection:
```csharp
services.AddGraphQL(b => b
    .AddSchema<StarWarsSchema>()
    .AddApolloTracing()
    .AddSystemTextJson());
```

### Conditional Metrics via HTTP Header

For production environments, you may want to enable metrics only for specific requests. This can be achieved by checking for a specific HTTP header:

```csharp
public static class GraphQLBuilderMetricsExtensions
{
    public static IGraphQLBuilder EnableMetricsByHeader(
        this IGraphQLBuilder builder, 
        string headerName = "X-GRAPHQL-METRICS")
    {
        return builder.ConfigureExecution(async (options, next) =>
        {
            if (!options.EnableMetrics)
            {
                var accessor = options.RequestServices.GetRequiredService<IHttpContextAccessor>();
                options.EnableMetrics = accessor.HttpContext.Request.Headers.ContainsKey(headerName);
            }
            return await next(options).ConfigureAwait(false);
        });
    }
}

// Usage in Startup.cs
services.AddGraphQL(b => b
    .AddSchema<StarWarsSchema>()
    .EnableMetricsByHeader()
    .AddSystemTextJson());
```

## Metrics Data Structure

The metrics data follows the Apollo Tracing format and includes:

1. Overall execution timing:
   - Start time
   - End time
   - Total duration

2. Phase timing:
   - Parsing duration
   - Validation duration
   - Execution duration

3. Resolver-level metrics:
   - Path in the query
   - Parent type
   - Field name
   - Return type
   - Start offset
   - Duration

Example metrics output:
```json
{
  "data": {
    "hero": {
      "name": "R2-D2",
      "friends": [
        {
          "name": "Luke"
        },
        {
          "name": "C-3PO"
        }
      ]
    }
  },
  "extensions": {
    "tracing": {
      "version": 1,
      "startTime": "2018-07-28T21:39:27.160902Z",
      "endTime": "2018-07-28T21:39:27.372902Z",
      "duration": 212304000,
      "parsing": {
        "startOffset": 57436000,
        "duration": 21985999
      },
      "validation": {
        "startOffset": 57436000,
        "duration": 21985999
      },
      "execution": {
        "resolvers": [
          {
            "path": ["hero"],
            "parentType": "Query",
            "fieldName": "hero",
            "returnType": "Character",
            "startOffset": 147389000,
            "duration": 2756000
          }
        ]
      }
    }
  }
}
```

## Usage Guidelines

1. **Development**: Enable metrics globally during development to identify performance bottlenecks.

2. **Production**: Use conditional enabling via HTTP headers to:
   - Minimize performance impact
   - Allow targeted performance analysis
   - Debug specific issues in production

3. **Monitoring**: Consider implementing a listener to:
   - Forward metrics to monitoring systems
   - Generate custom performance reports
   - Track long-running queries

## References

- [Apollo Tracing Specification](https://github.com/apollographql/apollo-tracing)
- [GraphQL.NET Documentation](https://graphql-dotnet.github.io/docs/getting-started/metrics)