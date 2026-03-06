# Field Middleware in GraphQL .NET

Field Middleware is a powerful feature in GraphQL .NET that allows you to inject behavior into the field resolution process. It acts as a pipeline component that wraps around field resolution, similar to how ASP.NET Core HTTP middleware works.

## Basic Implementation

To create field middleware, implement the `IFieldMiddleware` interface:

```csharp
public interface IFieldMiddleware
{
    ValueTask<object?> ResolveAsync(IResolveFieldContext context, FieldMiddlewareDelegate next);
}
```

The middleware delegate is defined as:

```csharp
public delegate ValueTask<object?> FieldMiddlewareDelegate(IResolveFieldContext context);
```

### Example Implementation

Here's an example of middleware that instruments field resolution for metrics:

```csharp
public class InstrumentFieldsMiddleware : IFieldMiddleware
{
    public ValueTask<object?> ResolveAsync(IResolveFieldContext context, FieldMiddlewareDelegate next)
    {
        return context.Metrics.Enabled
            ? ResolveWhenMetricsEnabledAsync(context, next)
            : next(context);
    }

    private async ValueTask<object?> ResolveWhenMetricsEnabledAsync(IResolveFieldContext context, FieldMiddlewareDelegate next)
    {
        var name = context.FieldAst.Name.StringValue;

        var metadata = new Dictionary<string, object?>
        {
            { "typeName", context.ParentType.Name },
            { "fieldName", name },
            { "returnTypeName", context.FieldDefinition.ResolvedType!.ToString() },
            { "path", context.Path },
        };

        using (context.Metrics.Subject("field", name, metadata))
            return await next(context).ConfigureAwait(false);
    }
}
```

## Registration Methods

There are two ways to register field middleware:

1. Using a middleware class:
```csharp
var schema = new Schema();
schema.Query = new MyQuery();
schema.FieldMiddleware.Use(new InstrumentFieldsMiddleware());
```

2. Using a middleware delegate:
```csharp
schema.FieldMiddleware.Use(next =>
{
    return context =>
    {
        // your code here
        var result = next(context);
        // your code here
        return result;
    };
});
```

## Dependency Injection

For dependency injection support, register the middleware in your schema constructor:

```csharp
public class MySchema : Schema
{
    public MySchema(
        IServiceProvider services,
        MyQuery query,
        InstrumentFieldsMiddleware middleware)
        : base(services)
    {
        Query = query;
        FieldMiddleware.Use(middleware);
    }
}
```

Register the middleware in your DI container:

```csharp
services.AddSingleton<InstrumentFieldsMiddleware>();
```

You can also register multiple middlewares using an enumerable:

```csharp
public class MySchema : Schema
{
    public MySchema(
        IServiceProvider services,
        MyQuery query,
        IEnumerable<IFieldMiddleware> middlewares)
        : base(services)
    {
        Query = query;
        foreach (var middleware in middlewares)
            FieldMiddleware.Use(middleware);
    }
}

// In Startup.cs
services.AddSingleton<ISchema, MySchema>();
services.AddSingleton<IFieldMiddleware, InstrumentFieldsMiddleware>();
services.AddSingleton<IFieldMiddleware, MyMiddleware>();
```

## Important Considerations

1. **One-time Application**: The default `DocumentExecuter` applies middlewares only once during schema initialization. Subsequent calls to `Use` methods have no effect.

2. **Lifetime Management**: Field middleware modifies field resolvers during application. Be careful with different lifetimes:

| Schema    | Graph Type | Middleware | Recommendation                                    |
|-----------|------------|------------|--------------------------------------------------|
| singleton | singleton  | singleton  | ✅ Safest and most performant (recommended)       |
| scoped    | scoped     | singleton  | ⚠️ Less performant                               |
| scoped    | scoped     | scoped     | ⚠️ Least performant                             |
| scoped    | singleton  | scoped     | ❌ Avoid - causes multiple middleware application |
| singleton | singleton  | scoped     | ❌ Cannot resolve scoped service from root        |

### Handling Scoped Dependencies

If your middleware needs scoped dependencies but uses singleton lifetime, obtain dependencies in the `Resolve` method:

```csharp
public class MyFieldMiddleware : IFieldMiddleware
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IMySingletonService _service;

    public MyFieldMiddleware(IHttpContextAccessor accessor, IMySingletonService service)
    {
        _accessor = accessor;
        _service = service;
    }

    public ValueTask<object?> ResolveAsync(IResolveFieldContext context, FieldMiddlewareDelegate next)
    {
        var scopedDependency1 = accessor.HttpContext.RequestServices.GetRequiredService<IMyService1>();
        var scopedDependency2 = accessor.HttpContext.RequestServices.GetRequiredService<IMyService2>();
        // Use dependencies
        return next(context);
    }
}
```

## Field Middleware vs Directives

- **Field Middleware**: Global component affecting all fields in the schema
- **Directives**: Target specific schema elements and are not limited to field resolvers

Choose field middleware for cross-cutting concerns that should apply globally, and directives for more targeted functionality.