# User Context in GraphQL.NET

User Context provides a way to pass custom data that can be accessed in field resolvers and validation rules. This is implemented through a dictionary-based class that inherits from `IDictionary<string, object?>`.

## Basic User Context

The simplest implementation involves creating a custom class:

```csharp
public class MyGraphQLUserContext : Dictionary<string, object?>
{
}
```

This can be used when executing queries:

```csharp
await schema.ExecuteAsync(_ =>
{
  _.Query = "...";
  _.UserContext = new MyGraphQLUserContext();
});
```

Field resolvers can then access this context:

```csharp
public class Query : ObjectGraphType
{
  public Query()
  {
    Field<DroidType>("hero")
      .Resolve(context =>
      {
        var userContext = context.UserContext as MyGraphQLUserContext;
        // Use userContext here
      });
  }
}
```

## HTTP User Claims Integration

For web applications needing access to HTTP request user claims, extend the UserContext class with a User property:

```csharp
public class MyGraphQLUserContext : Dictionary<string, object?>
{
    public ClaimsPrincipal User { get; set; }

    public MyGraphQLUserContext(ClaimsPrincipal user)
    {
        User = user;
    }
}
```

Configure this in your startup:

```csharp
services.AddGraphQL()
        .AddUserContextBuilder(httpContext => new MyGraphQLUserContext(httpContext.User));
```

Note: The `AddUserContextBuilder` method requires the `GraphQL.Server` package.

## Best Practices

1. Keep the UserContext focused on request-scoped data
2. Use it for passing user authentication/authorization information
3. Consider it for passing request-specific dependencies
4. Avoid storing long-lived or static data