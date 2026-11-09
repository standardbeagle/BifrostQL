---
title: "Server Operations: Your Own Secured GraphQL Fields"
description: "Add application-defined query and mutation fields to /graphql that run with the generated fields' security: grants, tenant scoping, audit identity and one transaction per mutation."
---

Generated fields cover single-table reads and writes. A business operation — price a claim
line, import a CSV of lines, renew a membership — needs code. A **server operation** is that
code as an ordinary root field of the endpoint's schema. It appears in introspection, it can
share one request with generated fields, and its errors reach the client the same way.

## Declare the operation in C#

```csharp
using BifrostQL.Core.Operations;
using BifrostQL.Core.Resolvers;

public sealed class ImportClaimLines : IBifrostOperation
{
    public string Name => "importClaimLines";
    public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
    public string Signature => "(claimId: Int!, csv: String!): ImportResult!";
    public string TypeDefinitions => "type ImportResult { imported: Int! claimTitle: String }";

    public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
    {
        var claimId = context.GetArgument<int>("claimId");

        // Read as the caller: another tenant's claim is simply not found.
        var claim = (await context.QueryAsync("claim", new Dictionary<string, object?> { ["id"] = claimId }))
            .SingleOrDefault() ?? throw new BifrostExecutionError("The claim is not available.");

        var imported = 0;
        foreach (var line in context.GetArgument<string>("csv")!.Split('\n'))
        {
            var cells = line.Split(',');
            // Write as the caller: tenant pinned, audit columns stamped, policy enforced.
            await context.InsertAsync("claim_line", new Dictionary<string, object?>
            {
                ["claim_id"] = claimId,
                ["code"] = cells[0],
                ["units"] = int.Parse(cells[1]),
            });
            imported++;
        }
        return new Dictionary<string, object?> { ["imported"] = imported, ["claimTitle"] = claim["title"] };
    }
}
```

- `Signature` is the field's SDL after its name: the argument list, then the return type.
- `TypeDefinitions` holds any types, inputs or enums the signature uses. A field of a declared
  object type resolves from a returned dictionary's key, or from a returned object's property.
- An operation is stateless. It gets its own dependencies from `context.Services`.

Register it:

```csharp
builder.Services.AddBifrostQL(o => o
    .BindStandardConfig(builder.Configuration)
    .AddOperation<ImportClaimLines>());
```

## Expose it with metadata

Registration alone does not publish anything. The endpoint lists the operations it exposes and
who may call them:

```text
:root { operations: importClaimLines[estimator], priceLine[estimator,manager] }
:root { operations-tenant: required }
```

| Key | Values | Meaning |
|---|---|---|
| `operations` | `name` or `name[grant,list]`, comma-separated | The operations in this endpoint's schema. A registered operation that is not listed is absent. A listed name with no registration fails the schema build. |
| `operations-tenant` | `required` / `optional` | `required` refuses a caller with no tenant claim (under `tenant-context-key`). Absent: `required` when any table declares `tenant-filter`, `optional` otherwise. |

A bracket uses the same grammar as `policy-actions` and the same rule as the
[policy engine](/BifrostQL/guides/authorization/): the caller must hold one of the listed roles
or permissions, or the admin role. An entry without a bracket admits every caller the endpoint
admits. A malformed entry or an empty bracket (`name[]`) fails model load.

Both checks run before `ExecuteAsync`. A refused caller gets
`Access denied: the caller may not run operation '…'`, and the operation's code never runs.

## What the context gives you

| Member | What it does |
|---|---|
| `GetArgument<T>(name)`, `HasArgument(name)` | The field's arguments |
| `Identity` | The caller as the policy engine sees them: id, tenant, roles, permissions |
| `UserContext` | The caller's user context, read-only |
| `Model`, `Services`, `CancellationToken` | The endpoint model, request services, request token |
| `QueryAsync(table, where, columns, limit, sort)` | Rows as the caller. See [Reading rows](#reading-rows). Tenant isolation, row scope, soft delete, column read guards and decryption apply. Rows are keyed by GraphQL field name. |
| `InsertAsync(table, values)` | One insert through the full mutation pipeline; returns the new key |
| `UpdateAsync(table, values)` | One update keyed by the key columns in `values`; returns the rows changed (0 when out of scope) |
| `DeleteAsync(table, key)` | One delete (soft where the table declares it); returns the rows affected |
| `LockRowAsync(table, key)` | Locks one row until the transaction ends. See [Serializing on a row](#serializing-on-a-row). |

There is no way to act as a different user, and no raw SQL. Every read goes through the read
seam protocol adapters use, and every write through the pipeline generated mutations use. The
one call that takes no caller scope is `LockRowAsync`: it returns no data and grants no write. A
query operation can only read; calling a write method or `LockRowAsync` from one is refused.

## Reading rows

```csharp
var latestStopped = await context.QueryAsync(
    "time_entries",
    where: new Dictionary<string, object?>
    {
        ["person_id"] = personId,                       // person_id = @p
        ["stopped_at"] = BifrostOperationContext.NotNull, // stopped_at IS NOT NULL
        ["note"] = null,                                // note IS NULL
    },
    columns: new[] { "id", "stopped_at" },
    limit: 1,
    sort: new[] { "stopped_at_desc", "id_desc" });
```

- `where` is an AND of column tests. There is no OR and no range: anything richer belongs in a
  view or a generated field.
- `sort` takes the tokens a generated field's `sort` argument takes: `<column>_asc` or
  `<column>_desc`, most significant first. A token without a direction, or for a column the table
  does not have, is refused. The primary key is appended as a tie-break.
- `limit` behaves as it does on a generated field. Leaving it out reads the default window of 100
  rows, **not every row**. `-1` reads up to the endpoint's `max-query-rows` ceiling, and a larger
  limit is capped at that ceiling.
- `columns` defaults to every column, so on a table with a read-denied column you must name the
  columns you want or the read is refused.
- A column named in `where` or `sort` passes the same read guard as a selected one. Testing or
  ordering by a column the caller may not read is refused with `ACCESS_DENIED`, because either
  would reveal it.

## Serializing on a row

An operation that reads to decide what to write can race. Two concurrent "start timer" calls both
read "nothing running", and the second insert fails on the unique index instead of stopping the
first entry. Lock a row they all share before the read:

```csharp
public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
{
    var personId = context.GetArgument<int>("personId");
    await context.LockRowAsync("people", new Dictionary<string, object?> { ["id"] = personId });

    var running = await context.QueryAsync(
        "time_entries",
        new Dictionary<string, object?> { ["person_id"] = personId, ["stopped_at"] = null },
        columns: new[] { "id" });
    foreach (var entry in running)
        await context.UpdateAsync("time_entries",
            new Dictionary<string, object?> { ["id"] = entry["id"], ["stopped_at"] = DateTime.UtcNow });

    await context.InsertAsync("time_entries", new Dictionary<string, object?> { ["person_id"] = personId });
    return running.Count;
}
```

The lock is held until the operation's transaction commits or rolls back. Concurrent calls wait
their turn, so of 20 concurrent starts, one entry is left running and 19 are stopped.

**Who may lock.** Operation code is trusted with its keys, so the lock takes no caller scope: a
member with no read or write on `people` can hold a lock on a `people` row, in any tenant. It
returns no data and grants nothing. The `UpdateAsync` beside it still runs as the caller. The one
thing it reveals is whether the row exists, so do not surface the not-found error for an id a
client chose unless that is acceptable.

**Errors.**

| Code | Constant | When |
|---|---|---|
| `LOCK_ROW_NOT_FOUND` | `BifrostOperationContext.LockRowNotFoundCode` | No row has that key. The message names the table, not the key. The operation may catch it and carry on. |
| `ROW_LOCK_UNSUPPORTED` | `BifrostOperationContext.RowLockUnsupportedCode` | The dialect has no row lock. Refused, so the operation never runs unserialized. |
| `PARTIAL_PRIMARY_KEY` | `MutationArgumentBinder.PartialPrimaryKeyCode` | `key` is anything other than the table's whole primary key, including a table with none. |

**By database.**

| Database | Statement | Note |
|---|---|---|
| PostgreSQL | `SELECT ... FOR UPDATE` | The login needs `UPDATE` on the table, not only `SELECT`. |
| MySQL | `SELECT ... FOR UPDATE` | Lock **before** any read. Under the default `REPEATABLE READ` the first plain read fixes the snapshot, so a read after a late lock still misses rows committed before it. |
| SQL Server | `SELECT ... WITH (UPDLOCK)` | |
| SQLite | none | Refused with `ROW_LOCK_UNSUPPORTED`. |

**What it does not do.**

- It serializes only the code paths that take the same lock. A generated `time_entries` update
  from elsewhere does not wait for it; keep a unique index as the backstop.
- On PostgreSQL an insert whose foreign key references the locked row waits until the lock is
  released, even from code that never asks for a lock. Keep the transaction short.
- An operation that locks several rows should lock them in the same order everywhere, or two
  operations can deadlock, and the database will fail one of them.
- Pass the key in the column's own .NET type: a `Guid` for a PostgreSQL `uuid`, not its text. Text
  fails with a database error, as it does for `UpdateAsync`.

## One transaction per mutation operation

A mutation operation's reads and writes share one connection and one transaction from the
endpoint's connection factory. The transaction commits when `ExecuteAsync` returns and rolls back
when it throws. The operation reads its own uncommitted writes. Database errors are reported
exactly as they are for a generated mutation.

- Mutation observers and state-transition observers run after the commit, never for a
  rolled-back write.
- If a write fails, the operation fails, even when its code catches the exception.
- Writes to an [approval-gated](/BifrostQL/guides/approval-workflows/) table are refused inside an
  operation. A gated write queues a pending change and reports it as an error, which cannot be
  one step of an all-or-nothing operation.

## Calling it

An operation is a normal field, so it can share a request with generated fields:

```graphql
mutation {
  importClaimLines(claimId: 7, csv: "A1,2\nB4,1") { imported claimTitle }
  claim(update: { id: 7, status: "priced" })
}
```

GraphQL runs mutation fields in order. Each one has its own transaction, so the generated
update commits separately from the operation.

## Limits

- Operations are GraphQL-only. Protocol adapters and MCP do not expose them.
- Multi-database endpoints (`AddBifrostEndpoints`) do not expose operations.
- Stored procedures and database functions are not exposed: `sp-include` is accepted, but no
  dialect loads procedures yet, and the operation context has no raw-SQL call. Logic that lives in
  a database function stays reachable only through a table the operation reads or writes
  (a view, or a trigger on a table it writes).

See the [design notes](https://github.com/standardbeagle/BifrostQL/blob/main/docs/superpowers/specs/2026-11-01-server-secured-operations-design.md)
for how this compares with the other extension points.
