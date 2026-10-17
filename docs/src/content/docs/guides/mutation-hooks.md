---
title: "Mutation Hooks: Timing, Applicability, and Soft-Delete"
description: "When before-commit and in-transaction mutation hooks run, how to read a generated insert id inside the transaction, why every hook must declare AppliesTo, and how soft-delete's DELETE-to-UPDATE rewrite changes what later code sees."
---

BifrostQL gives host code three places to act on a write. Three of their behaviours are easy to misread, and each one costs either correctness or performance when it is. This page states them from the code.

| Seam | Interface | Runs | Can abort? |
|------|-----------|------|-----------|
| Mutation transformer | `IMutationTransformer` | Before any SQL, in priority order | Yes (returned `Errors`) |
| Before-commit hook | `IBeforeCommitMutationHook` | Inside the transaction, immediately **before** the write | Yes (returned errors or a throw) |
| In-transaction hook | `IInTransactionMutationHook` | Inside the transaction, immediately **after** the write | Yes (a throw) |
| Mutation observer | `IMutationObserver` | After commit | No (failures are logged and swallowed) |

All four interfaces live in `BifrostQL.Core.Modules`.

## 1. Insert ids are knowable inside the transaction

A before-commit hook cannot see a generated id: it runs before the `INSERT`, and the pipeline passes it `Result = null`. A post-commit observer can see the id, but the transaction has already committed, so anything it writes is not atomic with the row.

`IInTransactionMutationHook.AfterWriteInTransactionAsync(MutationObserverContext)` runs between the two. The pipeline executes the write, then calls the hook on the same connection and transaction, with `context.Result` set to the write's result:

- **Insert:** the scalar the dialect's identity read returns. For a table with a single-column, database-generated key, that is the new key.
- **Update / delete:** the affected-row count. A count of `0` means the write was scoped away (tenant, policy, soft-delete filter) or matched nothing. Do not act on it.

For a composite key, or a key the client supplied, read the key columns from `context.Data` instead of `Result`. The built-in CDC outbox writer does the same: it takes the key from `Result` only on an insert into a single-key table.

`context.Data` holds the post-transformer payload, keyed by **database column names**.

The ambient state in this phase:

| Property | Value in the in-transaction phase |
|----------|-----------------------------------|
| `Connection` | The open connection carrying the write |
| `Transaction` | The `DbTransaction` wrapping the write. **`null` on the nested-mutation (tree-sync) path**, which runs its transaction as SQL `BEGIN`/`COMMIT` on the connection. A command run on `Connection` there still joins that transaction. Check `Connection`, not `Transaction`, to tell whether you are in a transaction. |
| `Dialect` | The `ISqlDialect` for that connection |
| `Model` | The `IDbModel` |
| `MutationState` | Per-action scratchpad shared with the same action's before-commit phase |
| `TransactionState` | Scratchpad shared by every action of the transaction (a batch's rows, a tree's nodes) |

The hook runs on every write path that executes a per-row write: single-row insert, update and delete (soft and hard); every row of a batch mutation; and every node of a nested tree mutation. It does **not** run when the approval gate diverts a write into a pending change (no write happened), or on a path that refuses or skips per-row hooks (see section 2).

### Example: create a dependent row with the new id

```csharp
using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

public sealed class OrderTimelineHook : IInTransactionMutationHook
{
    // One predicate, used by AppliesTo AND by the body's no-op check (section 2).
    private static bool IsOrders(IDbTable table) =>
        string.Equals(table.TableSchema, "dbo", StringComparison.OrdinalIgnoreCase)
        && string.Equals(table.DbName, "orders", StringComparison.OrdinalIgnoreCase);

    public bool AppliesTo(IDbTable table) => IsOrders(table);

    public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
    {
        if (!IsOrders(context.Table) || context.MutationType != MutationType.Insert)
            return;

        if (context.Connection is null || context.Dialect is null || context.Result is null)
            throw new BifrostExecutionError("Order timeline could not be recorded.");

        var dialect = context.Dialect;
        var p = dialect.ParameterPrefix;
        await using var cmd = context.Connection.CreateCommand();
        cmd.Transaction = context.Transaction; // null on tree-sync; the connection carries the transaction
        cmd.CommandText =
            $"INSERT INTO {dialect.TableReference("dbo", "order_timeline")} " +
            $"({dialect.EscapeIdentifier("order_id")}, {dialect.EscapeIdentifier("event")}) " +
            $"VALUES ({p}order_id, {p}event)";

        AddParameter(cmd, $"{p}order_id", context.Result);   // the generated orders.id
        AddParameter(cmd, $"{p}event", "created");
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }
}
```

**A throw rolls the write back.** The hook composite does not catch, so the exception leaves the transaction scope and the pipeline rolls back the `INSERT` together with anything the hook already wrote. Throw `BifrostExecutionError` to choose the message; any other exception type is wrapped as a database error.

### Registration

Register hooks as DI services. The pipeline resolves the composites `BeforeCommitMutationHooks` and `InTransactionMutationHooks`, which `AddBifrostQL` registers as **singletons** built from every registered `IBeforeCommitMutationHook` / `IInTransactionMutationHook`. Register yours as a singleton too, so the singleton composite does not capture a scoped instance:

```csharp
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
builder.Services.AddSingleton<IInTransactionMutationHook, OrderTimelineHook>();
```

The composite collects the hooks the first time it is resolved, so a hook registered after `AddBifrostQL` is still picked up. Hooks run in DI registration order: registered after `AddBifrostQL`, yours run after the built-ins it adds (change history, deferred effects, CDC outbox).

## 2. Hooks apply to every table unless you say otherwise

Both hook interfaces carry a default method:

```csharp
bool AppliesTo(IDbTable table) => true;
```

Two set-based write paths ask it, through `BeforeCommitMutationHooks.AnyApplies(table)` and `InTransactionMutationHooks.AnyApplies(table)`, because they cannot run per-row hooks:

- **Bulk batch fast path.** If any registered hook applies to the table, the batch runs row by row instead. The [bulk-batch reference](/BifrostQL/reference/bulk-batch-performance/) lists this among the fast-path gates.
- **Filtered update (`updateWhere`).** If any registered hook applies to the table, the request is refused with "Filtered update ... is not available while a mutation hook (approval, history, CDC) applies to this table".

The gate asks *applicability*, never *registration*. Every host registers the built-in hooks, and each one overrides `AppliesTo` to answer `true` only for tables carrying its own metadata (`emit-events`, history, approval, deferred). So in a host with only built-ins, the fast path stays open for every table that did not opt in.

**A host hook that does not override `AppliesTo` applies to every table.** One such hook turns off the bulk fast path, and refuses `updateWhere`, on every table in the model. That is fail-closed by design: running a hook on the slow path is only slower, while skipping a hook that should have run is a correctness bug (a missing audit row, a missing event).

### Narrowing correctly

Override `AppliesTo` with **the same predicate the hook's body uses to no-op**, as `OrderTimelineHook` does above with `IsOrders`. A table where `AppliesTo` returns `false` can take the bulk fast path, and on that path your hook does not run. If the two predicates drift, so that `AppliesTo` says `false` for a table the body would act on, those rows are written with no hook call. Nothing reports it.

Keep the predicate per **table**, not per operation. Applicability is asked once for a whole batch, which can mix inserts, updates and deletes. The built-in CDC writer follows this rule:

```csharp
public bool AppliesTo(IDbTable table) => CdcEventConfig.FromTable(table).EmitsEvents;
```

Its body no-ops on that same `EmitsEvents` check, then decides per operation.

## 3. Soft-delete rewrites DELETE to UPDATE inside the transformer chain

On a table with `soft-delete` metadata, `SoftDeleteMutationTransformer` (priority **100**) turns a `Delete` into an `Update` that stamps the soft-delete column (and `soft-delete-by`, when configured) and adds a "not already deleted" filter. The one exception is a permitted `_hardDelete`, which stays a real `DELETE`.

`MutationTransformersWrap.TransformAsync` runs transformers in ascending `Priority` order. Before each one it re-checks `AppliesTo(table, currentType, context)` against the **current** mutation type, and passes that type to `TransformAsync`. So after the soft-delete transformer:

- **Transformers with priority above 100** see `MutationType.Update`. A transformer that gates on `MutationType.Delete` never fires for a soft-deleted row.
- **Before-commit hooks, in-transaction hooks and post-commit observers** see `MutationType.Update` too. The soft-delete path builds its hook context as an update and notifies observers with `MutationType.Update`. The CDC outbox records it as an `update` event.

A transformer that must observe the original delete has to sit **strictly below 100**. Equal priorities have no guaranteed order. The built-in `AuditMutationTransformer` sits at 50 for this reason.

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;

public sealed class NoDeleteOfPostedInvoices : IMutationTransformer
{
    // Below SoftDeleteMutationTransformer (100): this sees MutationType.Delete
    // before the rewrite to Update.
    public int Priority => 90;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context) =>
        mutationType == MutationType.Delete
        && string.Equals(table.DbName, "invoices", StringComparison.OrdinalIgnoreCase);

    public ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table, MutationType mutationType,
        Dictionary<string, object?> data, MutationTransformContext context)
    {
        // data is keyed by database column names.
        var posted = data.TryGetValue("status", out var status) && Equals(status, "posted");
        return ValueTask.FromResult(new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            Errors = posted ? new[] { "Posted invoices cannot be deleted." } : Array.Empty<string>(),
        });
    }
}
```

```csharp
builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration)
    .AddMutationTransformer<NoDeleteOfPostedInvoices>());
```

Do not cross the 100 line casually. The band below 100 is the security band (tenant at 0, policy and state machine at 1–2, encryption at 40, audit at 50, concurrency at 60). A transformer there runs before soft-delete and before the data-filtering band. Keep it to the decision that needs the original verb.

### No public "logical verb" for hooks

Hooks get no public signal that an `Update` started as a soft delete. Internally the pipeline records the logical verb in `MutationState` for the approval gate, so a pending change is stored as a `delete`. That key and its accessors are `internal`, not a contract, so do not read them. A hook that must tell a soft delete from an ordinary update has to decide from the table's `soft-delete` metadata and the payload, or the decision belongs in a transformer below priority 100.

## See also

- [Extending the Pipeline with C# Hooks](/BifrostQL/guides/extensibility/) for the other extension points
- [Emitting Change Events (CDC)](/BifrostQL/guides/cdc-events/), the built-in in-transaction hook
- [Recording Change History](/BifrostQL/guides/change-history/), a hook that spans both in-transaction phases
- [Bulk batch performance](/BifrostQL/reference/bulk-batch-performance/) for the full fast-path gate list
