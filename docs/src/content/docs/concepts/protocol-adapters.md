---
title: "Protocol Adapters: One Pipeline, Many Front Doors"
description: "Why BifrostQL protocol adapters own only the wire and its codec, while every read and write — GraphQL or not — travels the same intent-executor pipeline with tenant isolation, soft-delete, and policy guards applied unconditionally."
---

BifrostQL can expose the same database through front doors other than GraphQL-over-HTTP: OData, gRPC, a custom binary framing, an in-process pipe — anything that can be decoded into a read or write request. Each front door is a **protocol adapter**. This page explains the architectural rule that makes adapters safe: *there is exactly one query pipeline, and no adapter is ever allowed to own a second one*.

If you want to build an adapter, see the [Protocol Adapter Authoring guide](/BifrostQL/guides/protocol-adapters/). This page covers why the seam is shaped the way it is. For the shipped adapters, see the [pgwire](/BifrostQL/guides/pgwire/), [RESP](/BifrostQL/guides/resp/), [S3 object endpoint](/BifrostQL/guides/s3/), and [MCP server](/BifrostQL/guides/mcp-server/) guides.

## The problem with a second data path

Every security promise BifrostQL makes — tenant isolation, soft-delete invisibility, policy row scope, column read guards, audit stamping, optimistic concurrency — is implemented as a transformer that runs *inside* the query and mutation pipeline. A protocol front end that opened its own database connection, or built its own SQL, would sit **beside** that pipeline instead of behind it. Every one of those promises would then need to be re-implemented, re-tested, and kept in sync inside the adapter — and the first time it drifted, the adapter would be a tenant-data leak with a novel wire format.

So the design forbids the situation outright. Four decisions, made once for the whole adapter surface, enforce it.

## The four decisions

### 1. Adapters own the wire and the codec — nothing else

An adapter's whole job is listening for requests in its own protocol and translating between that wire format and Bifrost's programmatic surface. Execution always crosses the shared seam: reads go through `IQueryIntentExecutor`, writes through `IMutationIntentExecutor`. An adapter never touches `SqlExecutionManager`, a `DbConnection`, or SQL text. The seam is deliberately the *only* execution surface handed to adapters, so "take a shortcut into core" is not an available API call.

### 2. Transformers apply inside the pipeline, where they cannot be bypassed

Filter transformers (tenant isolation, soft-delete, policy row scope, column read guards) are applied inside `SqlExecutionManager`; mutation transformers (tenant pinning, soft-delete rewrite, validation, audit, optimistic concurrency) inside `TableMutationPipeline`. The intent executors are thin compositions that delegate to exactly those two components — `QueryIntentExecutor` and `MutationIntentExecutor` deliberately have no code path that could run SQL without them. A request that reaches SQL has, by construction, already been transformed. There is no flag, parameter, or alternative overload that skips the chain.

### 3. Identity is projected through one shared, fail-closed factory

Every transport gate — the HTTP GraphQL middleware, the binary WebSocket middleware, and every protocol adapter — resolves caller identity through the single `IBifrostAuthContextFactory` service. One projection of principal → user context means the fail-closed semantics can never drift between front doors:

- an authenticated principal yields the full claim projection;
- an unauthenticated request yields an empty context — which downstream tenant transformers then refuse on tenant-filtered tables (`Tenant context required`), exactly as for an unauthenticated GraphQL request;
- a token from an OIDC issuer this deployment has no claim mapper for **throws** — never a degraded identity.

Adapters must not invent their own claim mapping; identity arrives on the adapter's wire and is projected through the factory.

### 4. Non-HTTP hosting goes through Kestrel connection handlers, and the contract has no `HttpContext`

Adapters are hosted as `IHostedService` instances (one wrapper per adapter): the host starts them during startup and stops them during graceful shutdown, and a `StartAsync` failure **aborts host startup** — a bind error never produces a host that looks healthy while its front door is dead. Raw TCP protocols bind their port through Kestrel's connection middleware (`ListenAnyIP(port, l => l.UseConnectionHandler<T>())`) rather than a hand-rolled `Socket` accept loop, so Kestrel owns accept, backpressure, and shutdown draining. And the adapter contract itself never sees an `HttpContext` — the intent APIs take plain data plus a user context, which keeps the seam honest for protocols where no HTTP request exists.

## What this buys you

**The security-transformer guarantee.** For any adapter — including ones written by third parties — the following hold without the adapter doing anything beyond using the seam:

- Tenant-filtered tables only ever return the caller's tenant rows; the tenant predicate is injected by the pipeline, not the adapter's codec.
- Soft-deleted rows never surface on reads; deletes on soft-delete tables become stamped updates.
- Inserts pin the caller's tenant even when the wire request claims another; cross-tenant updates and deletes match nothing (the same silent no-op the GraphQL path produces).
- Policy-denied columns are rejected whether selected *or* used as a filter (no boolean-oracle leak).
- All caller values bind as SQL parameters — the intent API accepts data structures, not SQL fragments, so there is nothing to concatenate.
- Missing identity fails closed instead of widening scope.

**Provable, per adapter.** Because the guarantee is a property of the seam, it can be asserted as a reusable test suite: every adapter derives the shared conformance kit (`BifrostQL.AdapterConformance`) and proves, over its *real* wire path, that tenant isolation, parameterization, policy guards, and the mutation transformer chain all hold. A new adapter doesn't argue it is safe — it demonstrates it, with the same facts every other adapter passes.

**One place to fix and extend.** A new security transformer, a bug fix in tenant scoping, an added audit rule — each lands once, in the pipeline, and applies to every front door simultaneously. Adapters never need a coordinated release to stay safe.

## Mental model

```
   GraphQL / HTTP ─┐
   Binary WS       ├──►  auth-context factory  ──►  intent executors ──► transformer
   OData adapter   │     (one identity seam,        (one execution        pipeline ──► SQL
   gRPC adapter   ─┘      fail closed)               seam)                (unskippable)
```

Many front doors; one hallway. The adapters differ only in what the doorway looks like from the street.
