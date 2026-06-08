---
title: Computed columns and server validation
description: Add SQL-based virtual fields, provider-backed .NET computed fields, and opt-in server validation to BifrostQL schemas.
---

BifrostQL can expose table-level virtual fields without changing the database schema. The same module surface also supports server-side mutation validation.

## SQL computed columns

Use `computed-sql` table metadata for simple cross-platform expressions. Each entry is:

```text
fieldName:GraphQlType:expression
```

Reference physical columns with `{column}` placeholders. BifrostQL resolves placeholders through the table schema and quotes them with the active SQL dialect.

```text
dbo.orders {
  computed-sql: totalWithTax:Float:({subtotal} + {tax})
}
```

`totalWithTax` is emitted as a selectable GraphQL field and projected in SQL as an expression. SQL computed fields are read-only; they are not added to mutation inputs.

## Provider computed columns

Use `computed-plugin` table metadata for .NET/provider-backed fields, including enrichment from remote APIs.

```text
dbo.orders {
  computed-plugin: shippingEstimate:String:shipping-api:depends=Id,destination_zip
}
```

Register an `IComputedColumnProvider` whose `Name` matches the metadata provider name:

```csharp
public sealed class ShippingEstimateProvider : IComputedColumnProvider
{
    public string Name => "shipping-api";

    public async ValueTask<object?> ComputeAsync(
        ComputedColumnContext context,
        CancellationToken cancellationToken = default)
    {
        var id = context.Row["Id"];
        var zip = context.Row["destination_zip"];
        // Call a remote service or local dependency here.
        return "2 business days";
    }
}
```

Provider fields are computed after the database query returns. If no `depends=` list is supplied, BifrostQL projects the table primary key columns so the provider has row identity.

## Server validation

Set `server-validation: enabled` on a table or column to enforce validation metadata during insert and update mutations:

```text
dbo.contacts { server-validation: enabled }
dbo.contacts.name { required: true }
dbo.contacts.age { min: 18 }
dbo.contacts.email {
  pattern: ^[^@]+@[^@]+\.[^@]+$
  pattern-message: Email must be valid.
}
```

Supported built-in rules are `required`, `min`, `max`, `minlength`, `maxlength`, `pattern`, and `pattern-message`.

For custom validation, use `validation-plugin` with registered `IServerValidationProvider` implementations:

```text
dbo.contacts { validation-plugin: custom-contact-rules }
```

```csharp
public sealed class ContactRules : IServerValidationProvider
{
    public string Name => "custom-contact-rules";

    public IReadOnlyList<string> Validate(ServerValidationContext context)
    {
        // Return zero or more error messages. Any error aborts the mutation.
        return Array.Empty<string>();
    }
}
```
