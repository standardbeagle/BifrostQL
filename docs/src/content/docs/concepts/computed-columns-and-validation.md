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

## File folder columns

Use `file-folder` table metadata to expose a storage folder as a read-only JSON column. This is useful for CMS, DAM, and other blob-backed content models where the database row owns a folder of files.

```text
dbo.pages {
  storage: bucket:/srv/cms;provider:local
  file-folder: assets:JSON:local:folder=assets/{Id},depends=Id,recursive=false
}
```

The emitted `assets` field returns file/folder entries with `name`, `key`, `isFolder`, `size`, `lastModified`, `contentType`, and `url` fields. The folder template can reference projected row values with `{ColumnName}` placeholders.

Built-in providers:

- `local` / `file-folder-local` — lists folders from the local filesystem storage bucket.
- `s3` / `file-folder-s3` — lists objects and common prefixes from S3 or S3-compatible storage.

You can configure the folder column inline:

```text
dbo.assets {
  file-folder: files:JSON:s3:folder=tenant/{tenant_id}/assets,depends=tenant_id,bucket=my-bucket,region=us-east-1,prefix=prod
}
```

Or use table/database `storage` metadata as the default bucket config and keep the folder column focused on the row-specific path.

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

    public async ValueTask<IReadOnlyList<string>> ValidateAsync(
        ServerValidationContext context,
        CancellationToken cancellationToken = default)
    {
        // Return zero or more error messages. Any error aborts the mutation.
        // Async lets you call a database or external policy service here.
        return Array.Empty<string>();
    }
}
```

Validation runs inside the mutation pipeline, so it applies to top-level *and* nested (tree-sync) writes alike. The same declarative rules (`required`, `min`, `pattern`, …) are derived once and exposed to generated client forms, keeping browser and server validation in lockstep. For the full hook surface — before-commit veto hooks, custom transformers, and DI registration — see [Extending BifrostQL](/BifrostQL/guides/extensibility/).
