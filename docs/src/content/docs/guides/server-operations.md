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
| `QueryAsync(table, where, columns, limit)` | Rows as the caller. `where` is an AND of column equalities. Tenant isolation, row scope, soft delete, column read guards and decryption apply. Rows are keyed by GraphQL field name. |
| `InsertAsync(table, values)` | One insert through the full mutation pipeline; returns the new key |
| `UpdateAsync(table, values)` | One update keyed by the key columns in `values`; returns the rows changed (0 when out of scope) |
| `DeleteAsync(table, key)` | One delete (soft where the table declares it); returns the rows affected |

There is no way to act as a different user, and no raw SQL. Every read goes through the read
seam protocol adapters use, and every write through the pipeline generated mutations use. A
query operation can only read; calling a write method from one is refused.

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
