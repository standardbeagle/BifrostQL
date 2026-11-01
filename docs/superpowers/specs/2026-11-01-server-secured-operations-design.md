# Server-secured operations: extension review and design

Date: 2026-11-01. Status: implemented on `feat/server-secured-operations`.

## Why

A feasibility check of BifrostQL 0.4.711 against PostgreSQL 17 found that an application cannot add
its own server-side business operation to `/graphql`. The probe got there only by decorating the DI
`IDocumentExecuter` and running a second GraphQL.NET schema whenever every root field was custom.
That route keeps Bifrost's security only because its resolvers call back into Bifrost. It also has
three limits: custom and generated fields cannot share one request, custom fields are absent from
introspection, and the route depends on internal behaviour. The operator asked for a supported way to
add server-secured logic, built on Bifrost's own extension points.

## Review: how an application extends Bifrost today

| Extension point | Registration | What it can do | What it cannot do |
|---|---|---|---|
| Filter transformer | `AddFilterTransformer<T>` | AND a row filter into every read of a table | add fields, write |
| Mutation transformer | `AddMutationTransformer<T>` | rewrite, veto or scope one write, inside its transaction | start another write, add fields |
| Before-commit / in-transaction hook | plain DI (`IBeforeCommitMutationHook`, `IInTransactionMutationHook`) | read or write on the write's own connection, or veto | add fields; no `Add…` helper |
| Mutation observer, query observer | DI / `AddQueryObserver<T>` | post-commit or post-query side effects; errors swallowed | change the result |
| Module API | `AddModuleApi<T>` (static registry) | add scalar **arguments** to existing table fields | add fields, types or resolvers |
| Computed column | `computed-expr` / `computed-sql` / `computed-plugin` + `IComputedColumnProvider` | a read-only per-row value | arguments, writes, transaction |
| Server validation | `IServerValidationProvider` (DI) | refuse a write with field errors | anything else |
| State machine | `state-column` / `transitions` metadata | legal transitions and role-gated transitions | rules about *who* beyond roles (e.g. approver is not the author) |
| Approval, deferred effects | metadata + built-in hooks | maker-checker queue; reversible change sets | host-defined operations; their `approve`/`undo` fields are hard-coded in `SchemaGenerator` and `BifrostDispatcher` |
| Protocol adapters | `AddProtocolAdapter<T>` + `IQueryIntentExecutor` / `IMutationIntentExecutor` | a new wire protocol over the secured read/write seams | anything on `/graphql`; each intent is its own transaction |
| Workflow sidecar | `IBifrostWorkflowExecutor` on an ASP.NET route | compose generated reads and writes under the caller's identity | atomicity (every call is its own transaction), `/graphql`, introspection |
| MCP tools | `AddBifrostMcpTools(json)` | declarative single-table query and write tools | C# handlers |
| Stored procedures | `sp-include` / `sp-exclude` | nothing in a live host | — `DbModelLoader.BuildModel` passes an empty procedure list and no dialect reads procedures, so the SDL generator, resolver and filters for procedures never run |
| Metadata keys | rule strings, `IMetadataSource` | every built-in key; host keys only with the `x-` prefix | host-validated keys |

Findings that shaped the design:

- The SDL and the resolver map are closed. `SchemaGenerator.SchemaTextFromModel` builds the SDL as
  text, `BifrostDispatcher.BuildResolverMap` is a fixed map, and `DbSchema.FromModel` is the only
  place the two meet. Nothing a host registers can reach them.
- Every write opens its own connection and transaction
  (`MutationCommandExecutor.RunInTransactionAsync`). No ambient transaction exists for a composite
  operation to join, so the sidecar pattern's "a client cannot do half a renewal" is not true.
- `BifrostExecutionError.ErrorCode` never reaches the GraphQL wire: the dispatcher rethrows
  `new ExecutionError(message, inner)`, and GraphQL.NET derives `BIFROST_EXECUTION_ERROR` from the
  inner exception type. The documented `ACCESS_DENIED` and `PENDING_APPROVAL` codes reach only the
  adapters that map `ErrorCode` themselves.
- Several docs describe code that does not exist (`BasicAuditModule`, `AddModules`,
  `userContext.ToAppIdentity()`, `IQueryObserver.OnAfterExecuteAsync`), and stored procedures are
  documented as loadable.

## Design

One new extension point, made from the pieces Bifrost already has. It is not a new framework.

### C#: `IBifrostOperation`

```csharp
public interface IBifrostOperation
{
    string Name { get; }                      // root field name
    BifrostOperationKind Kind { get; }        // Query or Mutation
    string Signature { get; }                 // "(lineId: Int!): Float"
    string TypeDefinitions => string.Empty;   // extra SDL types the signature names
    ValueTask<object?> ExecuteAsync(BifrostOperationContext context);
}
```

Registered with `AddBifrostQL(o => o.AddOperation<PriceLine>())`. The SDL is emitted by
`SchemaGenerator` next to the generated fields, and the field is wired through `BifrostDispatcher`.
The operation is therefore an ordinary field of the one schema: it appears in introspection, can
share a request with generated fields, and its errors take the dispatcher's error path.

### Metadata: exposure, grants, tenant rule

```text
:root { operations: priceLine[estimator,manager], importClaimLines[estimator] }
:root { operations-tenant: required }
```

- `operations` lists the operations an endpoint exposes. An operation registered in C# but not
  listed is absent from the schema, so exposure fails closed. A listed name with no registration fails
  the schema build. A bracket is a grant list, parsed by the same bracket grammar as
  `policy-actions` and matched by the same grant rule as the policy evaluator, admin bypass included.
- `operations-tenant` is `required` or `optional`. `required` refuses a caller with no tenant claim,
  read under the model's `tenant-context-key`. When the key is absent, the default is `required` if
  any table declares `tenant-filter`, and `optional` otherwise.

Both checks run in the resolver **before** the operation's code, so the operation cannot skip them.

### Request context: `BifrostOperationContext`

The context gives the operation its arguments, the caller's user context and identity, the model,
the request services and the cancellation token. It also gives secured data access:

- `QueryAsync(table, where, columns, limit)` runs through `ISqlExecutionManager.ExecuteIntentAsync`,
  the read seam protocol adapters use, so tenant, policy, soft-delete, column guards and crypto
  projection all apply.
- `InsertAsync` / `UpdateAsync` / `DeleteAsync` run through `TableMutationPipeline`, the pipeline
  every generated mutation uses: the transformer chain, before-commit and in-transaction hooks,
  history, the deferred and CDC hooks, and the audit actor resolved from the same user context.

The identity is not a parameter of these calls. The context always passes the request's own user
context, so an operation cannot read or write as anyone else.

### One transaction per mutation operation

A mutation operation runs inside an `OperationWriteScope`. The scope is one connection and one
transaction, opened from the endpoint's connection factory exactly as a generated mutation opens
its own. The pipeline's `RunInTransactionAsync` and the intent read both join the scope when one is
present. Post-commit notifications (mutation observers, state-transition observers) are queued and
run only after the scope commits. Any exception rolls the whole operation back. A database
exception is mapped by `BifrostExecutionError.FromDatabaseException`, as on every generated write.
A query operation gets no scope, and its reads use the normal read path.

Writes to an approval-gated table are refused inside an operation. The gate's divert commits a
pending row and then fails the write, and that cannot coexist with an all-or-nothing operation.

### Not in this change

- **Stored procedures and functions through config.** The resolver and SDL generator exist, but there
  is no procedure reader for any dialect, and the resolver applies none of the security pipeline
  (own connection, no tenant, no audit). Reviving them is a separate slice. For now, an operation
  that needs a function can call it with its own SQL and its own tenant check.
- **Multi-database endpoints** (`BifrostMultiDbOptions`) do not expose operations.
- **MCP / adapters**: operations are GraphQL-only.

## Test plan

SQLite integration facts in `BifrostQL.Core.Test`:

- the field is in introspection, with its real argument and return types;
- one request combining an operation and a generated field;
- a caller without the grant, or without a tenant claim, is refused before the operation runs;
- reads and writes made through the context are tenant-scoped and audit-stamped as the caller;
- a failing step rolls back the earlier writes of the same operation;
- an unlisted operation is absent, and a listed but unregistered operation fails the build.

`BifrostQL.PublicApi.Test` compiles an operation written against public types only, as a
third-party author would.
