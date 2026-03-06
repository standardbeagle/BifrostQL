# GraphQL.NET Authorization

BifrostQL uses GraphQL.NET's authorization capabilities to secure access to data. This document outlines the authorization approach and implementation details.

## Overview

Authorization in BifrostQL is implemented using GraphQL.NET's validation rules and metadata-based permissions system. This allows for:

- Field-level permission requirements
- Operation-level authentication checks
- Flexible permission validation through claims

## Implementation Details

### Permission Requirements

Permissions can be applied at both the type level and field level:

```csharp
public class MyGraphType : ObjectGraphType
{
    public MyGraphType()
    {
        // Require permission for all fields in this type
        this.RequirePermission("READ_ONLY");
        
        // Require additional permission for specific field
        Field(x => x.Secret).RequirePermission("Admin");
    }
}
```

### Validation Rule

The validation rule runs before query execution to enforce authentication and permission requirements:

```csharp
public class RequiresAuthValidationRule : IValidationRule
{
    public Task<INodeVisitor> ValidateAsync(ValidationContext context)
    {
        var userContext = context.UserContext as GraphQLUserContext;
        var authenticated = userContext.User?.IsAuthenticated() ?? false;

        return Task.FromResult(new EnterLeaveListener(_ =>
        {
            // Validate mutations require authentication
            _.Match<Operation>(op =>
            {
                if (op.OperationType == OperationType.Mutation && !authenticated)
                {
                    context.ReportError(new ValidationError(
                        context.Document.Source,
                        "6.1.1",
                        $"Authorization is required to access {op.Name}.",
                        op) { Code = "auth-required" });
                }
            });

            // Validate field-level permissions
            _.Match<Field>(fieldAst =>
            {
                var fieldDef = context.TypeInfo.GetFieldDef();
                if (fieldDef.RequiresPermissions() &&
                    (!authenticated || !fieldDef.CanAccess(userContext.User.Claims)))
                {
                    context.ReportError(new ValidationError(
                        context.Document.Source,
                        "6.1.1",
                        $"You are not authorized to run this query.",
                        fieldAst) { Code = "auth-required" });
                }
            });
        }));
    }
}
```

### Permission Extension Methods

The following extension methods are used to manage permissions:

```csharp
public static class GraphQLExtensions
{
    public static readonly string PermissionsKey = "Permissions";

    // Check if type requires any permissions
    public static bool RequiresPermissions(this IProvideMetadata type)
    {
        var permissions = type.GetMetadata<IEnumerable<string>>(PermissionsKey, new List<string>());
        return permissions.Any();
    }

    // Check if user has all required permissions
    public static bool CanAccess(this IProvideMetadata type, IEnumerable<string> claims)
    {
        var permissions = type.GetMetadata<IEnumerable<string>>(PermissionsKey, new List<string>());
        return permissions.All(x => claims?.Contains(x) ?? false);
    }

    // Check for specific permission
    public static bool HasPermission(this IProvideMetadata type, string permission)
    {
        var permissions = type.GetMetadata<IEnumerable<string>>(PermissionsKey, new List<string>());
        return permissions.Any(x => string.Equals(x, permission));
    }

    // Add required permission
    public static void RequirePermission(this IProvideMetadata type, string permission)
    {
        var permissions = type.GetMetadata<List<string>>(PermissionsKey);

        if (permissions == null)
        {
            permissions = new List<string>();
            type.Metadata[PermissionsKey] = permissions;
        }

        permissions.Add(permission);
    }

    // Fluent API for adding permission to field builder
    public static FieldBuilder<TSourceType, TReturnType> RequirePermission<TSourceType, TReturnType>(
        this FieldBuilder<TSourceType, TReturnType> builder, string permission)
    {
        builder.FieldType.RequirePermission(permission);
        return builder;
    }
}
```

## Usage

To implement authorization in BifrostQL:

1. Define permissions required for types and fields using `RequirePermission`
2. Register the `RequiresAuthValidationRule` with your schema
3. Ensure your user context includes authentication state and claims
4. Configure any middleware needed for authentication

## References

- [GraphQL.NET Authorization Project](https://github.com/graphql-dotnet/authorization)
- [Authorization.AspNetCore Project](https://github.com/graphql-dotnet/server/tree/develop/src/Authorization.AspNetCore)