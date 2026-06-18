---
title: Extending BifrostQL — Hooks & Providers
description: Plug your own C# logic into the BifrostQL pipeline — filter transformers, mutation transformers, before-commit veto hooks, async server validation, computed columns, and query observers. Generic AddBifrostQL registration, no boilerplate.
---

BifrostQL generates the whole GraphQL API from your schema, but real apps need *their own* rules: a filter the metadata can't express, a mutation that must be vetoed under a business condition, an async call to an external service before a write commits. Every one of these is a small, typed C# class that you register with one line.

This guide covers the **programming surface**. For the zero-code, metadata-driven modules (tenant isolation, soft-delete, audit columns), see the [Module System](/BifrostQL/guides/modules/).

## Registering your code

Every extension point has a generic `Add…<T>` overload on the `AddBifrostQL` options. The type is resolved from DI, so your class can take constructor dependencies:

```csharp
using BifrostQL.Server;

builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration)
    .AddFilterTransformer<RegionFilter>()
    .AddMutationTransformer<InvoiceLockTransformer>()
    .AddQueryObserver<SlowQueryLogger>());
```

| Method | Registers | Interface |
|--------|-----------|-----------|
| `AddFilterTransformer<T>()` | A WHERE-clause injector | `IFilterTransformer` |
| `AddMutationTransformer<T>()` | A mutation rewriter | `IMutationTransformer` |
| `AddQueryObserver<T>()` | A lifecycle side-effect | `IQueryObserver` |

:::tip[Built-ins register themselves]
The shipped transformers (tenant filter, auto-filter, soft-delete, audit, policy, state machine, enum value, server validation) **auto-register from metadata**. You only call `Add…<T>()` for your own classes — built-ins are wired up the moment their metadata key appears.
:::

Need to register a collection built at runtime? Non-generic overloads take instances or a factory:

```csharp
o.AddFilterTransformers(sp => BuildTenantFilters(sp));      // Func<IServiceProvider, IReadOnlyList<IFilterTransformer>>
o.AddMutationTransformers(new[] { new AuditMutationTransformer() });
```

## Filter transformers

A filter transformer injects a `WHERE` clause on every read of a table. Extend `SingleColumnFilterTransformerBase` for the common single-column case:

```csharp
public sealed class RegionFilter : SingleColumnFilterTransformerBase
{
    // metadata key it reacts to, and a priority (lower = applied closer to the query)
    public RegionFilter() : base("region-filter", priority: 150) { }

    protected override TableFilter BuildFilter(IDbTable table, string column, IBifrostContext ctx)
        => TableFilter.Equals(column, ctx.UserContext["region"]);
}
```

Throwing from a filter transformer aborts the query — use that to fail closed on missing security context.

**Priority ranges** decide nesting order:

- `0–99` — security and tenant isolation (applied innermost)
- `100–199` — data filtering (soft-delete, region)
- `200+` — application filters

## Mutation transformers

A mutation transformer can rewrite or block a mutation before SQL is generated. The pipeline is **async** — implement `TransformAsync`:

```csharp
public sealed class InvoiceLockTransformer : MetadataMutationTransformerBase
{
    public InvoiceLockTransformer() : base("invoice-lock", priority: 200) { }

    protected override async ValueTask<MutationTransformResult> TransformCoreAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        if (mutationType == MutationType.Update && await IsPostedAsync(data, context))
            return MutationTransformResult.Reject("Posted invoices cannot be edited.");

        return MutationTransformResult.Unchanged(data);
    }
}
```

A transformer can change the operation type — that is exactly how soft-delete turns a `DELETE` into an `UPDATE`.

:::note[Async migration]
The pipeline interface is `IMutationTransformer.TransformAsync` (previously the synchronous `Transform`). Server validation is likewise `IServerValidationProvider.ValidateAsync`. If you have older synchronous modules, port them to the `…Async` signature.
:::

## Before-commit veto hooks

Filter and mutation transformers run *before* SQL executes. A **before-commit hook** runs *inside the open transaction*, after the rows are staged but before the commit — the last place to enforce an invariant that depends on the database state the mutation just produced.

```csharp
public sealed class CreditLimitHook : IBeforeCommitMutationHook
{
    public async ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
    {
        var balance = await context.ScalarAsync<decimal>(
            "SELECT SUM(amount) FROM orders WHERE customer_id = @id",
            ("@id", context.Row["customer_id"]));

        return balance > 10_000m
            ? new[] { "Order would exceed the customer credit limit." }
            : Array.Empty<string>();   // empty = allow the commit
    }
}
```

Return a non-empty list (or throw) and the whole transaction rolls back; an empty list lets the commit proceed. Register the hook in DI:

```csharp
builder.Services.AddScoped<IBeforeCommitMutationHook, CreditLimitHook>();
```

This is the right tool for cross-row business rules — "no two active managers per team", "stock must stay non-negative" — that can only be checked once the write is applied.

## Async server validation

For field- and row-level validation that may need I/O (uniqueness checks, an external policy service), implement `IServerValidationProvider` and point a table at it with the `validation-plugin` metadata key:

```csharp
public sealed class ContactRules : IServerValidationProvider
{
    public string Name => "custom-contact-rules";

    public async ValueTask<IReadOnlyList<string>> ValidateAsync(
        ServerValidationContext context,
        CancellationToken ct = default)
    {
        if (await EmailExistsAsync(context.Values["email"], ct))
            return new[] { "Email is already registered." };

        return Array.Empty<string>();
    }
}
```

```text
dbo.contacts { validation-plugin: custom-contact-rules }
```

The same rule set powers both **server enforcement** and **client exposure** — declarative rules (`required`, `min`, `max`, `pattern`, …) are derived once and surfaced to generated forms, so the browser and the server never drift. See [Computed Columns & Validation](/BifrostQL/concepts/computed-columns-and-validation/).

## Query observers

Observers are pure side effects — logging, metrics, tracing. They fire at four phases and **cannot modify the query**; an observer that throws is logged and the query continues.

```csharp
public sealed class SlowQueryLogger : IQueryObserver
{
    public ValueTask OnAfterExecuteAsync(QueryObserverContext ctx)
    {
        if (ctx.Elapsed > TimeSpan.FromSeconds(1))
            _log.LogWarning("Slow query on {Table}: {Ms}ms", ctx.Table, ctx.Elapsed.TotalMilliseconds);
        return ValueTask.CompletedTask;
    }
}
```

| Phase | When |
|-------|------|
| `Parsed` | After the GraphQL request becomes the internal query tree |
| `Transformed` | After all filter/mutation transformers have run |
| `BeforeExecute` | Immediately before SQL execution |
| `AfterExecute` | After SQL execution completes |

## Nested mutations run the full pipeline

When you write a parent row and its children in one mutation (tree-sync), **every nested operation flows through the same transformer pipeline** and the whole tree commits in **one SQL-level transaction** (`BEGIN … COMMIT`, rolled back on any failure). Tenant filters, soft-delete rewrites, audit columns, validators, and before-commit hooks all apply to children exactly as they do to top-level rows — there is no "back door" that skips your rules.

## Fail fast on bad config

BifrostQL validates all metadata at **model build time**, not on the first request. A typo in a `computed-sql` expression, an `auto-filter` mapping that names a missing column, or a malformed state-machine transition aborts startup with a single error naming the schema, table, key, and offending value — so misconfiguration surfaces in CI, not in production traffic.

## See also

- [Module System](/BifrostQL/guides/modules/) — the metadata-driven, zero-code modules
- [State Machines](/BifrostQL/guides/state-machines/) — lifecycle enforcement built on the mutation pipeline
- [Computed Columns & Validation](/BifrostQL/concepts/computed-columns-and-validation/) — virtual fields and declarative rules
