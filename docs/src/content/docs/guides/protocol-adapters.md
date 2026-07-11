---
title: "Authoring a Protocol Adapter"
description: "Implement IProtocolAdapter to expose BifrostQL over a non-GraphQL protocol: register with AddProtocolAdapter, project identity through IBifrostAuthContextFactory, execute via the query/mutation intent executors, host raw TCP under Kestrel, and prove safety with the shared conformance kit."
---

A protocol adapter lets BifrostQL answer requests on a wire that isn't GraphQL-over-HTTP — OData, gRPC, a custom binary framing, an in-process pipe. This guide walks the full authoring surface: the hosting contract, registration, identity, the intent APIs, Kestrel hosting for raw TCP, and the conformance suite every adapter must pass.

Read the [concept page](/BifrostQL/concepts/protocol-adapters/) first if you haven't: the short version is that **an adapter owns only its wire and codec**. Reads execute through `IQueryIntentExecutor`, writes through `IMutationIntentExecutor`, and identity is projected through `IBifrostAuthContextFactory` — never through your own claim mapping, `SqlExecutionManager`, or a database connection.

## The contract

```csharp
public interface IProtocolAdapter
{
    /// Begins accepting protocol requests. Called once by the host during
    /// startup; a thrown exception aborts host startup.
    Task StartAsync(CancellationToken cancellationToken);

    /// Stops accepting protocol requests and drains in-flight work. Called
    /// once by the host during graceful shutdown.
    Task StopAsync(CancellationToken cancellationToken);
}
```

Each registered adapter is wrapped in its own `IHostedService`, so its lifecycle is the host's lifecycle. Two rules follow:

- **Fail fast in `StartAsync`.** A port bind failure, bad certificate, or missing dependency must throw — that aborts host startup instead of leaving a healthy-looking host with a dead front door.
- **No `HttpContext` dependency.** The contract has none on purpose; identity arrives on your wire and is projected via the auth-context factory (below).

## Registration

Register the adapter type on the setup options. It is resolved from DI (constructor injection works) and started/stopped by the host.

Single database:

```csharp
builder.Services.AddBifrostQL(o => o
    .AddProtocolAdapter<MyProtocolAdapter>());
```

Multi-database endpoints:

```csharp
builder.Services.AddBifrostEndpoints(o =>
{
    o.AddEndpoint(e => { e.ConnectionString = "..."; e.Path = "/graphql"; });
    o.AddEndpoint(e => { e.ConnectionString = "..."; e.Path = "/reporting"; });
    o.AddProtocolAdapter<MyProtocolAdapter>();
});
```

Both `BifrostSetupOptions.AddProtocolAdapter<T>()` and `BifrostMultiDbOptions.AddProtocolAdapter<T>()` are additive and idempotent per type.

With multiple endpoints registered, every intent must name its target endpoint path (`Endpoint = "/reporting"`); with a single endpoint, `null` selects it. An unknown path fails fast — there is no fallback to a different database.

## Identity: the auth-context factory

Resolve `IBifrostAuthContextFactory` from DI and project every request's principal through it. It is the same seam the HTTP GraphQL and binary WebSocket gates use, so fail-closed semantics (including the unmapped-OIDC-issuer throw) hold identically on your wire:

```csharp
// The HttpContext here is only the carrier the factory contract requires —
// no HTTP request exists. RequestServices must be set so OIDC claim-mapper
// resolution (and its fail-closed unmapped-issuer path) works as on HTTP gates.
var identityCarrier = new DefaultHttpContext { RequestServices = _services };
if (principal is not null)
    identityCarrier.User = principal;
var userContext = _authFactory.CreateUserContext(identityCarrier);
```

An authenticated principal yields the full claim projection; an unauthenticated request yields an empty context, which the tenant transformers downstream refuse on tenant-filtered tables. A second overload, `CreateUserContext(context, existing)`, merges entries your frontend already parsed from the wire — identity-derived keys always win.

Never map claims yourself and never fabricate tenant/user keys in the user context: that is exactly the drift the shared factory exists to prevent.

## Reads: the query intent API

`IQueryIntentExecutor` executes a programmatic `GqlObjectQuery` — no GraphQL text, no parser:

```csharp
public interface IQueryIntentExecutor
{
    Task<IDbModel> GetModelAsync(string? endpoint = null);
    Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken ct = default);
}
```

Build the query tree against the model returned by `GetModelAsync` for the **same endpoint**, so `GqlObjectQuery.DbTable` references the cached model the intent executes against:

```csharp
var model = await _executor.GetModelAsync(request.Endpoint);
var table = model.GetTableFromDbName(request.Table);   // unknown table throws

var query = new GqlObjectQuery
{
    DbTable = table,
    SchemaName = table.TableSchema,
    TableName = table.DbName,
    GraphQlName = table.GraphQlName,
    Path = table.GraphQlName,
};
foreach (var column in request.Columns)
    query.ScalarColumns.Add(new GqlObjectColumn(column));
if (request.Filter is not null)
    query.Filter = TableFilter.FromObject(request.Filter, table.DbName);

var result = await _executor.ExecuteAsync(new QueryIntent
{
    Query = query,
    UserContext = userContext,
    Endpoint = request.Endpoint,
}, cancellationToken);
```

`QueryIntentResult` carries:

- `Rows` — the root result set as column-name → value dictionaries, converted with the same coercions GraphQL responses get;
- `TotalCount` — unpaged count, populated only when the intent set `GqlObjectQuery.IncludeResult`;
- `Sql` — the executed parameterized SQL text (placeholders, never inlined values) for diagnostics and auditing.

Execution routes through `SqlExecutionManager.ExecuteIntentAsync`, so every registered filter transformer and column read guard applies unconditionally — your adapter has no API surface to skip them.

## Writes: the mutation intent API

`IMutationIntentExecutor` is the write counterpart. Intents are plain data:

```csharp
var result = await _mutationExecutor.ExecuteAsync(new MutationIntent
{
    Table = "orders",                         // database table name; unknown → throws
    Action = MutationIntentAction.Update,     // Insert | Update | Delete
    Data = values,                            // column values, GraphQL-field or db-column
                                              // names, case-insensitive
    PrimaryKey = new object?[] { id },        // optional positional PK values,
                                              // declared key-column order (composite-safe)
    UserContext = userContext,
    Endpoint = request.Endpoint,
}, cancellationToken);
```

Semantics mirror the GraphQL mutation fields:

- **Insert** — `Data` carries the new row. `PrimaryKey` is *invalid* on insert and fails fast (accepting-and-ignoring it could silently turn an intended update into a duplicate insert). Returns the generated identity.
- **Update** — `Data` carries the SET columns; address the row via `PrimaryKey` (wins over key columns present in `Data`). A table with a concurrency token requires the token's last-read value in `Data`. Returns the key (single-key) or affected count (composite-key).
- **Delete** — `Data` carries predicate columns, `PrimaryKey` overlays them (the same merge GraphQL's `_primaryKey` performs, arity-checked). Returns the affected row count — `0` when a tenant or policy scope made the write a no-op.

Execution routes through `TableMutationPipeline` — the seam shared with the GraphQL resolver — so the full mutation transformer chain (policy, state machine, enum mapping, validation, soft delete, tenant isolation, audit, optimistic concurrency) applies unconditionally.

A read-only adapter simply never takes a dependency on `IMutationIntentExecutor`.

## Hosting a raw (non-HTTP) TCP protocol

Do not open a bare `Socket` accept loop. Bind the port through Kestrel's connection middleware and let a connection handler decode frames and delegate to the adapter:

```csharp
webBuilder.ConfigureKestrel(kestrel =>
    kestrel.ListenAnyIP(9090, listen =>
        listen.UseConnectionHandler<MyProtocolConnectionHandler>()));
```

Kestrel then owns socket accept, backpressure, and shutdown draining; your adapter's `StartAsync`/`StopAsync` manage only protocol-level state (codec tables, session registries, …).

## Worked example: the Echo adapter

The repository ships a reference adapter with **zero wire code** — `EchoProtocolAdapter` in `tests/BifrostQL.Server.Test/ProtocolAdapterTests.cs`. Its "wire" is a plain request object (table, columns, filter, principal, endpoint), which makes it the smallest complete demonstration of the pattern:

1. Constructor takes `IQueryIntentExecutor`, `IMutationIntentExecutor`, `IBifrostAuthContextFactory`, and `IServiceProvider` from DI.
2. `StartAsync`/`StopAsync` track lifecycle; requests before start (or after stop) are refused.
3. Each read projects the principal through the auth factory, builds a `GqlObjectQuery` against `GetModelAsync`'s model, and executes a `QueryIntent`.
4. Each write projects identity the same way and executes a `MutationIntent`.

A real adapter is exactly this shape plus listening and a codec. The accompanying `ProtocolAdapterTests` prove the hosting contract (started once, stopped once, `StartAsync` failure aborts host startup) and that tenant isolation holds end to end.

## Proving safety: the conformance kit

Every adapter must pass the shared security-conformance suite in `tests/BifrostQL.AdapterConformance` instead of copying its tests. A passing suite proves the adapter is not a security hole around the GraphQL pipeline: security transformers apply, SQL is parameterized, policy read guards hold (including the filter-as-oracle case), missing identity fails closed, and — for write-capable adapters — mutations run the transformer chain.

Derive the suite in your adapter's test project:

```csharp
public sealed class MyAdapterConformanceTests : ProtocolAdapterConformanceTests
{
    // Register the adapter on the fixture's endpoint options.
    protected override void RegisterAdapter(BifrostMultiDbOptions options)
        => options.AddProtocolAdapter<MyAdapter>();

    // Encode the request in YOUR wire format, send it through the adapter's
    // real request path, decode the rows. Server-side rejections must
    // propagate as exceptions carrying the server's error text — a suite
    // that swallows errors cannot prove fail-closed behavior.
    protected override Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
        ConformanceReadRequest request) { ... }

    // Write-capable adapters only:
    protected override bool AdapterSupportsMutations => true;
    protected override Task<object?> ExecuteMutationAsync(
        ConformanceMutationRequest request) { ... }
}
```

The base class owns the whole fixture: a per-suite in-memory SQLite database, the security metadata rules (`tenant-filter`, `soft-delete`, `policy-read-deny`), the test host, and a SQL-capture observer it asserts against. Your derivation supplies only the wire translation.

**The mutation opt-out is for genuinely read-only adapters.** `AdapterSupportsMutations` defaults to `false`, which skips the mutation facts — legitimate when your adapter exposes no write surface at all. An adapter that exposes *any* write surface must return `true` and override `ExecuteMutationAsync`; opting out while shipping writes would leave the write path unproven against tenant isolation and soft-delete semantics.

`EchoProtocolAdapterConformanceTests` in `tests/BifrostQL.Server.Test` is the reference derivation — about forty lines, since the base class supplies every fact.

## Checklist

- [ ] `IProtocolAdapter` implemented; `StartAsync` throws on bind/listen failure.
- [ ] Registered via `AddProtocolAdapter<T>()` (single or multi-db options).
- [ ] Identity projected through `IBifrostAuthContextFactory` — no custom claim mapping.
- [ ] Reads only via `IQueryIntentExecutor`, writes only via `IMutationIntentExecutor`.
- [ ] No reference to `SqlExecutionManager`, `DbConnection`, or SQL text anywhere in the adapter.
- [ ] Raw TCP hosted under a Kestrel connection handler, not a bare socket loop.
- [ ] Conformance suite derived and green; `AdapterSupportsMutations` matches the adapter's real write surface.
