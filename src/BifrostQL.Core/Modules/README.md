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
