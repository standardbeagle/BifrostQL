---
title: "gRPC Schema Contract: Descriptor Strategy & the Field-Number Manifest"
description: "The architectural decision behind BifrostQL's gRPC surface — why the descriptor is built dynamically from the cached DbModel but numbered from a checked-in field-number manifest, so the wire contract stays stable across schema drift without a code-generation step."
---

This is an **architecture decision record**. It fixes the compatibility model of
BifrostQL's gRPC surface *before* any runtime server is written, so that the
first line of wire-facing code inherits a stable contract instead of
back-filling one later. No runtime server, generated client, mutation RPC, or
gRPC-Web is introduced here (see [Non-goals](#non-goals)); this slice decides
**how the descriptor is produced and how field numbers stay stable**, nothing
more.

## Context

Two facts about how BifrostQL already works constrain every option, and one
existing piece of code shows the trap we must design around.

### The schema is a runtime artifact, cached per profile

BifrostQL has no mapping files and no build-time schema: it reads the live
database at startup and builds a `DbModel`, then a GraphQL `ISchema`, from that
read (see [Schema Generation](/BifrostQL/concepts/schema-generation/)). That
build is memoized by `ProfileModelCache`: a single shared DB read is turned into
a `(IDbModel Model, ISchema Schema)` pair **once per profile** — via a
`Lazy<T>` under `ExecutionAndPublication` — and served from cache for the life of
the connection. Different profiles project different subsets of the same base
schema (metadata hides tables and columns), and a schema change is only picked
up on reconnect / restart (`ProfileModelCache.Reset`).

Any gRPC descriptor must be produced from **the same cached `DbModel`**, keyed
the same way per profile, and must not introduce a second, differently-timed
source of truth for "what the schema is."

### Hosting is Kestrel connection handlers, not a bespoke socket loop

A non-GraphQL front door is an `IProtocolAdapter` (see
[Protocol Adapters](/BifrostQL/concepts/protocol-adapters/)). It owns only its
wire and codec; it reads through `IQueryIntentExecutor` and projects identity
through `IBifrostAuthContextFactory`, so the security-transformer pipeline
applies unconditionally. It is hosted as an `IHostedService` (one wrapper per
adapter) and — for a raw protocol like gRPC's HTTP/2 framing — binds its port
through Kestrel's connection middleware rather than a hand-rolled `Socket`
accept loop. The contract never sees an `HttpContext`. The pgwire, RESP, and S3
adapters are the precedent. A gRPC adapter, when it exists, is *this* shape; the
descriptor strategy must not assume ASP.NET's `Grpc.AspNetCore` service
plumbing, which would smuggle an `HttpContext`-shaped dependency back in.

### The trap: positional field numbers already exist in the tree

`BifrostQL.Core/Schema/ProtoSchemaGenerator.cs` already emits `proto3` from a
`DbModel`. It is the seed of this surface — and it demonstrates precisely the
bug this ADR forbids. It assigns field numbers **positionally**:

```csharp
var fieldNumber = 1;
foreach (var column in table.Columns) {
    // ...
    sb.AppendLine($"  {optional}{protoType} {column.GraphQlName} = {fieldNumber};");
    fieldNumber++;
}
```

The number a column gets is a function of its *ordinal position in the current
read*. Insert a column in the middle, drop a column, or have the database return
columns in a different order, and every following field silently renumbers. In
protobuf the field number **is** the wire identity — renumbering is a
silent, backward-incompatible break that no test in `ProtoSchemaGeneratorTests`
catches, because every fixture regenerates both sides from the same model. The
same fragility applies to the `BifrostMessage` `oneof` slot each table receives
(also positional). It also maps `decimal → double`, discarding exact decimal
precision. **This ADR exists to replace positional numbering with a stable,
checked-in contract before any client depends on it.**

## Options considered

### Option A — Dynamic descriptor registration from the cached `DbModel`

Build a `FileDescriptorSet` (the protobuf descriptor, the same structure a
`.proto` compiles to) at runtime from the cached `DbModel`, alongside the
existing `ISchema` build in `ProfileModelCache`, and register it with the gRPC
runtime's reflection/registry. Clients discover message and service shapes via
server reflection or a fetched descriptor; there is no `.proto` file compiled
into client stubs at BifrostQL build time.

- **Fits the product.** The schema is already a runtime artifact read from the
  DB; the descriptor is produced the same way, at the same time, from the same
  cached model, per profile. No new build step, no second source of truth.
- **Reuses `ProfileModelCache`.** The descriptor becomes a third memoized output
  of the same per-profile build that already produces `(model, schema)` — same
  lifetime, same reset-on-reconnect, same concurrency guarantees.
- **Per-profile projection is natural.** A profile that hides a column simply
  omits it from *its* descriptor while the field number stays reserved (see
  below), because the number comes from the manifest, not the projection.
- **Cost:** field numbers are *not* implied by anything in the model, so they
  must come from an external, stable source — the manifest is mandatory, not
  optional. Standard gRPC codegen clients need a descriptor fetch/reflection
  step rather than a checked-in `.proto`.

### Option B — Build-time snapshot source generation

Emit a `.proto` file (and C# stubs) at BifrostQL build time from a representative
schema, check the generated artifacts into the repo, and serve from the compiled
descriptors.

- **Standard client story.** A checked-in `.proto` is what most gRPC toolchains
  expect; clients compile it directly.
- **Fights the product.** BifrostQL's schema is *not* known at BifrostQL build
  time — it is the customer's database, read at their startup, filtered by their
  profiles. A build-time snapshot freezes a schema that does not exist yet and
  cannot match an arbitrary deployment. It reintroduces exactly the
  code-generation step [Schema Generation](/BifrostQL/concepts/schema-generation/)
  exists to avoid.
- **Staleness by construction.** Any additive column requires regenerating and
  re-shipping stubs; the descriptor and the live `DbModel` drift the moment a
  customer alters a table. The per-profile projection has no build-time
  representation at all.
- **Still needs the manifest.** Even a snapshot must number fields stably across
  regenerations, so Option B does not avoid the manifest — it only adds a build
  step on top of it.

## Decision

**Adopt Option A: build the descriptor dynamically from the cached `DbModel`,
and make a checked-in JSON field-number manifest the single source of truth for
field (and `oneof` slot) numbers.** The descriptor's *shape* is discovered at
runtime from the live schema; the descriptor's *numbering* is fixed forever by
the manifest. This decouples "what the schema looks like now" from "what number
each column has always had," which is the property positional numbering lacks.

Rationale: Option A is the only choice consistent with BifrostQL's defining
constraint — the schema is the customer's runtime database, not a build-time
input — and it reuses `ProfileModelCache` rather than bolting a parallel,
differently-timed pipeline beside it. The manifest supplies the one thing the
runtime model cannot: a stable identity for each field that survives column
reordering, addition, and removal. Option B's only advantage (a checked-in
`.proto`) is available to clients anyway via reflection or a descriptor
endpoint, and it does not justify freezing a schema that is inherently dynamic.

### Contract specification

**Deterministic service and message names.** Names derive *only* from the
schema-derived, already-deduplicated `GraphQlName` of each table and column —
never from ordinal position, never from raw DB identifiers.

| Element | Name | Source |
|---------|------|--------|
| Row message | `{Table.GraphQlName}Row` | table GraphQlName (matches existing generator) |
| Read request | `Get{Table.GraphQlName}Request` | table GraphQlName |
| Read response | `Get{Table.GraphQlName}Response` | table GraphQlName |
| Service | `BifrostQuery` | fixed; one `Get{Table.GraphQlName}` RPC per table |

Because `GraphQlName` is deterministic and collision-resolved at model build
time (`ColumnDto.DeduplicateGraphQlNames`), two builds of the same schema
produce byte-identical names regardless of column read order.

**Composite-PK request shape.** A read request identifies a row by its **full**
primary key. The request message carries **one field per key column**, in
manifest order — an AND of all key columns, mirroring the GraphQL
`buildPkEqFilter` rule and the
[composite-PK compliance](/BifrostQL/concepts/schema-generation/) invariant that
a key is *never* index-zero-reduced to its first column. `Table.KeyColumns`
(all columns where `IsPrimaryKey`) drives this. A request that omits any key
column matches zero rows (fail-closed); the adapter builds no predicate of its
own — it supplies the positional key values to `IQueryIntentExecutor` and lets
the pipeline scope the read. Single-column, composite, and BigInt keys all use
the same shape; there is no single-key fast path to drift from.

**proto3 presence.** Nullable columns use `proto3` **explicit presence** (the
`optional` keyword), so a database `NULL` is wire-distinguishable from a typed
default (`0`, `""`, `false`). Non-nullable columns use implicit presence. This
matches the existing generator's `optional`-on-nullable behavior and must be
preserved: collapsing NULL into the default is a data-fidelity bug for
nullable numeric and boolean columns.

**Decimal mapping.** `decimal` / `numeric` / `money` map to proto **`string`**
carrying the canonical decimal text — **not** `double`. This *corrects* the
existing `decimal → double` mapping, which silently loses precision on monetary
and high-scale values. It is the same "carry exact numerics as decimal strings,
never as a lossy float" rule the client stack already follows for BigInt/Decimal
key round-trips. `bigint` remains `int64` (exact on the protobuf wire; the
JavaScript precision hazard lives only in JSON transcoding, addressed where a
JSON/gRPC-Web surface is actually built — out of scope here).

**Checked-in field-number manifest.** A JSON file, versioned into the repo, is
the authoritative map from `(message, field-name)` to field number and the
record of reserved numbers. JSON is chosen deliberately: it is tooling- and
LLM-consumable, diff-reviewable, and the natural format for a machine-owned
contract artifact. Illustrative shape:

```json
{
  "manifestVersion": 1,
  "messages": {
    "UsersRow": {
      "fields": { "Id": 1, "Name": 2, "Email": 4 },
      "reserved": [3],
      "reservedNames": ["PhoneNumber"]
    }
  },
  "envelope": {
    "BifrostMessage": {
      "tableSlots": { "Users_row": 2, "Orders_row": 3 },
      "reserved": []
    }
  }
}
```

At descriptor build time, each column's number is **looked up** in the manifest,
never derived from position. A column present in the model but absent from the
manifest is a new column and is assigned the next free number (see drift rules);
a manifest entry absent from the model is a removed column whose number stays
reserved. Generation reads the manifest, reconciles it against the cached
`DbModel`, and either emits a descriptor with stable numbers or **fails** (never
silently renumbers).

### Schema-drift semantics

The manifest makes drift behavior explicit and enforceable rather than emergent
from read order:

| Schema change | Field-number behavior |
|---------------|----------------------|
| **Additive column** | Gets the next unused number for its message; **every existing number is preserved unchanged.** Manifest gains one entry. |
| **Column removal** | Its number moves to the message's `reserved` list and its name to `reservedNames`; the number is **never reused.** The descriptor emits a `reserved N;` / `reserved "name";` so protobuf itself enforces non-reuse. |
| **Column rename** | Treated as remove + add: the old number is reserved, the renamed column receives a **new** number. Names are never "moved" onto an existing number. |
| **Incompatible type change** (e.g. a manifest number typed `int32` now maps to a `string` column) | Generation **fails** — the number cannot change wire type. Resolution is to reserve the old number and allocate a new one, making the break explicit and reviewable. |
| **New / removed table** | The `oneof` slot in `BifrostMessage` is manifest-tracked identically: new tables take the next free slot; removed tables' slots are reserved. |
| **Column hidden by a profile** | Omitted from *that profile's* projected descriptor, but its number stays reserved — a hidden column never frees its number for another column. |
| **No structural change** | Numbers are byte-stable across builds regardless of DB column read order. |

The invariants, stated once: **additive changes preserve every existing number;
rename and removal never reuse a number; an incompatible type change on an
existing number fails generation.** These are guaranteed by construction because
the number comes from the manifest, not from the model's shape.

## Non-goals

This slice decides the compatibility model only. Explicitly **out of scope**:

- **No runtime gRPC server.** No `IProtocolAdapter` is wired to Kestrel; no port
  is bound; no RPC is served. This ADR constrains that code before it is
  written.
- **No generated client.** No client stubs, SDK, or descriptor-fetch tooling
  ships here.
- **No mutation RPC.** The contract describes reads (row messages,
  `Get{Table}` shape) only. Writes route through `IMutationIntentExecutor` when a
  later slice adds them and are governed by the adapter write invariants — not
  designed in this slice.
- **No gRPC-Web.** Browser-facing gRPC-Web framing and the JSON-transcoding
  number/precision concerns it raises are a separate, later decision.
