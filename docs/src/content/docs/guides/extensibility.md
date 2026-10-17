---
title: "Extending the Pipeline with C# Hooks"
description: "Plug your own C# logic into the BifrostQL pipeline: before-commit veto hooks, async server validation, provider-backed computed columns, and query observers."
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

A mutation transformer can rewrite or block a mutation before SQL is generated. The pipeline is **async** — implement `IMutationTransformer.TransformAsync`. Return a `MutationTransformResult` carrying the (possibly rewritten) `MutationType` and `Data`; a non-empty `Errors` array aborts the mutation. `data` is keyed by database column names.

```csharp
// Your own service; AddMutationTransformer<T>() registers T as a DI singleton,
// so constructor dependencies are resolved for you.
public interface IInvoiceLedger
{
    ValueTask<bool> IsPostedAsync(object? invoiceId);
}

public sealed class InvoiceLockTransformer : IMutationTransformer
{
    private readonly IInvoiceLedger _ledger;
    public InvoiceLockTransformer(IInvoiceLedger ledger) => _ledger = ledger;

    public int Priority => 200;

    // Only tables that declare `invoice-lock` metadata, and only updates.
    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) =>
        mutationType == MutationType.Update && table.Metadata.ContainsKey("invoice-lock");

    public async ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        data.TryGetValue("id", out var id);
        var posted = await _ledger.IsPostedAsync(id);

        return new MutationTransformResult
        {
            MutationType = mutationType,   // unchanged
            Data = data,                   // unchanged
            Errors = posted ? new[] { "Posted invoices cannot be edited." } : Array.Empty<string>(),
        };
    }
}
```

For a synchronous transformer gated on one metadata key whose value names a column, extend `MetadataMutationTransformerBase` instead: pass the key and priority to its constructor, implement `ModuleName`, and override `TransformCore(table, mutationType, data, context, columnName)`. Its `AppliesTo` already checks the key is present.

A transformer can change the operation type — that is exactly how soft-delete turns a `DELETE` into an `UPDATE`. Soft-delete runs at priority 100, and every later transformer, hook and observer sees the rewritten `Update`; a transformer that must see the original `Delete` needs a priority below 100 (see [Mutation Hooks](/BifrostQL/guides/mutation-hooks/#3-soft-delete-rewrites-delete-to-update-inside-the-transformer-chain)).

### Reading inside the write transaction

Every transformer chain on a write path — single-row insert, update, delete and upsert, the per-row batch path, `updateWhere`, and tree-sync — runs inside the write's own transaction, and `MutationTransformContext` hands it over: `Connection`, `Transaction`, `Dialect` and `CancellationToken`. A guard that depends on database state reads through them instead of opening its own connection, so it sees rows the same mutation already wrote and its read commits or rolls back with the write.

To read a row and keep it from changing until commit, use `LoadRowForUpdateAsync`. It matches the row by column equality (database column names) and takes the dialect's update lock — `WITH (UPDLOCK)` on SQL Server, `FOR UPDATE` on PostgreSQL and MySQL, nothing on SQLite, which serializes writers:

```csharp
var evt = await context.LoadRowForUpdateAsync(
    eventsTable, new[] { "capacity" }, new Dictionary<string, object?> { ["id"] = data["event_id"] });
if (evt is null) // answer a missing row exactly like an out-of-scope one
    return new MutationTransformResult
    {
        MutationType = mutationType,
        Data = data,
        Errors = new[] { "The event is not available." },
    };
```

The predicate is exactly the columns you pass; it is not narrowed by tenant or policy scope, so apply your own and answer "missing" and "not yours" with the same response.

Two paths differ. Tree-sync manages its transaction with SQL `BEGIN`/`COMMIT`, so `Transaction` is null while `Connection` is set; commands run on the connection's ambient transaction. The bulk set-based batch fast path transforms rows before its transaction exists, so `Connection` is null there. A transformer that reads through the connection overrides `RequiresWriteTransaction(table)` — using the same predicate its body uses — and the batch then stays on the per-row path for that table. `LoadRowForUpdateAsync` refuses to run without a write transaction rather than reading outside it.

:::note[Async migration]
The pipeline interface is `IMutationTransformer.TransformAsync` (previously the synchronous `Transform`). Server validation is likewise `IServerValidationProvider.ValidateAsync`. If you have older synchronous modules, port them to the `…Async` signature.
:::

## Before-commit veto hooks

Filter transformers run *before* SQL executes; mutation transformers run inside the write's transaction but before its SQL. A **before-commit hook** runs *inside the open transaction*, immediately **before** the write. It sees the transformed payload (`context.Data`, keyed by database column names) and the open `context.Connection` / `context.Transaction`, but not the write's result: `context.Result` is always `null` here.

```csharp
public sealed class CreditLimitHook : IBeforeCommitMutationHook
{
    private static bool IsOrders(IDbTable table) =>
        string.Equals(table.DbName, "orders", StringComparison.OrdinalIgnoreCase);

    public bool AppliesTo(IDbTable table) => IsOrders(table);

    public async ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
    {
        if (!IsOrders(context.Table) || context.MutationType != MutationType.Insert
            || context.Connection is null || context.Dialect is null)
            return Array.Empty<string>();

        var p = context.Dialect.ParameterPrefix;
        await using var cmd = context.Connection.CreateCommand();
        cmd.Transaction = context.Transaction;
        cmd.CommandText = $"SELECT COALESCE(SUM(amount), 0) FROM orders WHERE customer_id = {p}id";
        var id = cmd.CreateParameter();
        id.ParameterName = $"{p}id";
        id.Value = context.Data["customer_id"] ?? DBNull.Value;
        cmd.Parameters.Add(id);

        var existing = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        var amount = Convert.ToDecimal(context.Data["amount"]);
        return existing + amount > 10_000m
            ? new[] { "Order would exceed the customer credit limit." }
            : Array.Empty<string>();   // empty = allow the write
    }
}
```

Return a non-empty list (or throw) and the whole transaction rolls back before the write runs; an empty list lets the write proceed. Register the hook in DI as a singleton (the hook composite `AddBifrostQL` registers is itself a singleton):

```csharp
builder.Services.AddSingleton<IBeforeCommitMutationHook, CreditLimitHook>();
```

This is the right tool for business rules checked against current database state — "stock must stay non-negative", "credit limit not exceeded". To act on the write's result inside the same transaction (the generated id of an insert), use an `IInTransactionMutationHook` instead. Both hook types apply to **every table** unless they override `AppliesTo`, which disables the bulk-batch fast path and `updateWhere` model-wide. See [Mutation Hooks: Timing, Applicability, and Soft-Delete](/BifrostQL/guides/mutation-hooks/).

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
- [Mutation Hooks](/BifrostQL/guides/mutation-hooks/) — hook timing, insert ids in the transaction, `AppliesTo`, soft-delete ordering
- [State Machines](/BifrostQL/guides/state-machines/) — lifecycle enforcement built on the mutation pipeline
- [Computed Columns & Validation](/BifrostQL/concepts/computed-columns-and-validation/) — virtual fields and declarative rules
