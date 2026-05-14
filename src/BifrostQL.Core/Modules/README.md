# BifrostQL Module System

The module system provides hooks for cross-cutting concerns through an event-driven architecture.

## Module Types

### Filter Transformers (`IFilterTransformer`)

Inject additional WHERE clause filters into queries. Used for:
- Tenant isolation
- Soft-delete filtering
- Row-level security
- Custom data filtering

**Base Classes:**
- `SingleColumnFilterTransformerBase` - For single-column filters based on metadata
- `ContextValueFilterTransformerBase` - For filters that need values from user context

**Priority Ranges:**
- 0-99: Security/tenant filters (innermost)
- 100-199: Data filtering (soft-delete, etc.)
- 200+: Application-specific filters

### Mutation Transformers (`IMutationTransformer`)

Transform mutation operations before execution. Used for:
- Converting DELETE to UPDATE (soft-delete)
- Adding audit timestamps
- Validating mutation data

**Base Classes:**
- `MetadataMutationTransformerBase` - For metadata-driven transformations
- `SoftDeleteMutationTransformerBase` - For soft-delete implementations

### Query Observers (`IQueryObserver`)

Lifecycle hooks for side effects. Used for:
- Auditing
- Metrics collection
- Caching
- Logging

**Phases:**
- `Parsed` - Query parsed from GraphQL
- `Transformed` - After all transformers applied
- `BeforeExecute` - SQL built, about to execute
- `AfterExecute` - Execution complete, results available

### Mutation Modules (`IMutationModule`)

Modify mutation data before execution. Used for:
- Auto-populating audit columns
- Setting default values

## Authorization Policy Engine

The policy engine is an always-on, opt-in-per-table authorization layer. A table
is governed by policy only when it carries `policy-*` metadata; a table with no
policy metadata is unrestricted (the documented opt-in default). It is split
across two transformers that share one `TablePolicy` (parsed by
`PolicyConfigCollector`):

- `PolicyFilterTransformer` (`IFilterTransformer`, priority 1) — the query path.
  Enforces table read-deny, compiles the row-scope expression into a filter
  ANDed alongside the tenant filter, and — through the `IColumnReadGuard` seam
  called by `QueryTransformerService` — enforces column read-deny.
- `PolicyMutationTransformer` (`IMutationTransformer`, priority 1) — the
  mutation path. Enforces table action-deny, column write-deny, and row scope on
  update/delete.

Both run inside the 0-99 security range, immediately after the tenant filter at
priority 0. Identity is read from the per-request user context (`user_id`,
`roles`). The `admin` role bypasses every check.

**Enforcement mechanism — reject, not hide.** A request that references a denied
table, column, or action is rejected with a clear, generic error rather than
having the denied field silently stripped. Every policy error message is
non-leaking: it never names the table, column, or action, so error output cannot
be used to probe the schema. Enforcement lives in the query/mutation pipeline,
so a direct GraphQL request cannot bypass it — there is no UI-only check.

### Policy metadata

| Key | Scope | Effect |
|-----|-------|--------|
| `policy-actions` | table | Comma-separated permitted actions: `read`, `create`, `update`, `delete`. Unrecognized tokens are ignored. |
| `policy-read-deny` | table | Comma-separated columns that may not be read. A query selecting one is rejected. |
| `policy-write-deny` | table | Comma-separated columns that may not be written. A mutation writing one is rejected. |
| `policy-row-scope` | table | Row-scope expression `column = {context-key}`, ANDed onto every read and onto update/delete. |

```csharp
// dbo.orders permits read + update only; the ssn column is read-denied;
// non-admins are scoped to their own tenant's rows.
"dbo.orders { policy-actions: read,update }"
"dbo.orders { policy-read-deny: ssn }"
"dbo.orders { policy-row-scope: tenant_id = {tenant_id} }"
```

Both policy transformers are registered automatically by the BifrostQL host —
no explicit `AddFilterTransformer`/`AddMutationTransformer` call is needed.

## Creating a Filter Transformer

Using the base class:

```csharp
public sealed class MyFilterTransformer : SingleColumnFilterTransformerBase
{
    public MyFilterTransformer() : base("my-metadata-key", priority: 100)
    {
    }
    
    public override string ModuleName => "my-filter";
    
    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        var value = GetValueFromContext(context);
        return TableFilterFactory.Equals(table.DbName, columnName, value);
    }
}
```

## Creating a Mutation Transformer

Using the soft-delete base class:

```csharp
public sealed class MySoftDeleteTransformer : SoftDeleteMutationTransformerBase
{
    public MySoftDeleteTransformer() : base("deleted_at_column", priority: 100)
    {
    }
    
    public override string ModuleName => "my-soft-delete";
    
    protected override MutationTransformResult TransformDelete(
        IDbTable table,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName,
        TableFilter softDeleteFilter)
    {
        var transformedData = new Dictionary<string, object?>(data)
        {
            [columnName] = DateTimeOffset.UtcNow
        };
        
        return new MutationTransformResult
        {
            MutationType = MutationType.Update,
            Data = transformedData,
            AdditionalFilter = softDeleteFilter
        };
    }
}
```

## Registration

Register modules in your service configuration:

```csharp
builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration)
    .AddFilterTransformer<MyFilterTransformer>()
    .AddMutationTransformer<MyMutationTransformer>()
    .AddQueryObserver<MyQueryObserver>());
```
