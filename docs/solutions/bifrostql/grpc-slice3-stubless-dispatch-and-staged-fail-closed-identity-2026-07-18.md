---
written_at: 2026-07-18T08:38:29Z
source_event: task:01KXM41DXBANVVP5B8HZJTTNYV
module: bifrostql
category: protocol-adapter-pattern
confidence: high
sources:
  - task:01KXM41DXBANVVP5B8HZJTTNYV
  - git:58744e5
  - git:f976027
  - git:f6ac114
  - .claude/rules/protocol-adapter-security.md
tags: [protocol-adapter, grpc, reflection, identity, fail-closed, dynamic-dispatch, seam-pattern]
status: steering
recurrence: 1
---

# gRPC slice 3: stub-less dynamic dispatch + staged fail-closed identity

gRPC slice 3 (task `01KXM41DXBANVVP5B8HZJTTNYV`, review PASS attempt 1, 0
blockers, no rewinds) hosts a runtime-generated gRPC service set (from
`DbModel`, no compiled `.proto` stubs) over Kestrel HTTP/2. Two techniques
here are new to the adapter catalog and reusable by any future dynamic-schema
front door; a third confirms an existing invariant generalizes.

## 1. Stub-less dynamic gRPC dispatch

`BifrostDynamicGrpcService` registers `Method<byte[],byte[]>` per table via
`IServiceMethodProvider<T>` — Grpc.AspNetCore's low-level hook that lets a
host build its service/method set at runtime instead of from generated
stubs. Pair this with a codec that encodes/decodes straight from contract
field metadata (`GrpcMessageCodec`, `CodedInputStream`/`CodedOutputStream`)
and dispatch needs zero compiled `.proto` files — the whole service surface
is derived from `DbModel` at startup.

**Trap avoided**: built-in `AddGrpcReflection` cannot filter descriptors
per-identity — it serves one static `FileDescriptorSet` to every caller.
`GrpcReflectionService` is a custom subclass built specifically so
reflection routes through `GrpcSchemaVisibility`/`PolicyEvaluator` — the SAME
per-identity authorization the data path uses (this is invariant 4 in
`.claude/rules/protocol-adapter-security.md`, applied to a *dynamic*
descriptor set rather than a static catalog: a denied table/column is
omitted from the descriptor entirely, and a denied symbol lookup returns
`NOT_FOUND`, not a distinguishable error — no enumeration oracle).

Reusable pattern for any adapter that needs to expose a dynamically-derived
schema over a stub-based RPC framework: (a) build methods/services from the
domain model at runtime via the framework's lowest-level registration hook,
never generated stubs; (b) if the framework ships a reflection/introspection
add-on, assume it is identity-blind and subclass/replace it to route through
the same authorization gate as reads.

## 2. Fail-closed identity staged ahead of the credential-extraction slice

Slice 3 wires reads through `IBifrostAuthContextFactory` +
`IQueryIntentExecutor` now, even though bearer-token *parsing* is a later
slice (4). With no credential extraction yet, every call resolves an empty
auth context — which the shared pipeline already treats as fail-closed
(`PermissionDenied` / disjoint empty scope), not as "trust everyone until
auth ships." `GrpcFailClosedTests` proves this non-vacuously: anonymous
List/Get against a tenant-scoped table returns `PermissionDenied`, not rows;
cross-tenant `Get` returns null indistinguishable from missing.

The reusable sequencing lesson: when a protocol adapter's hosting slice
necessarily lands before its auth slice, wire the **fail-closed identity
seam** (the shared factory + intent executor) in the hosting slice itself —
do not defer it to the auth slice. Credential extraction (parsing a bearer
token, TLS client cert, etc.) is additive in the later slice; there is never
an intermediate window where the adapter is open-anonymous because "auth
isn't built yet." This generalizes invariant 4's shared-gate requirement
across slice boundaries, not just across op classes within one slice.

## 3. Confirms: single-funnel error mapping generalizes to a third adapter

`GrpcStatusMapper.GuardAsync` wraps every op class (Get/List/Stream +
reflection) in one funnel: `BifrostExecutionError` and all non-adapter
exceptions map to generic `INTERNAL` (detail logged server-side only,
verified absent from the wire message in tests); only the adapter-owned
`GrpcRequestException` is forwarded verbatim. This is the same shape as the
OData epic-close single-funnel finding
(`docs/solutions/bifrostql/odata-epic-close-single-funnel-error-mapping-2026-07-18.md`)
and pgwire invariants 1/9 in `.claude/rules/protocol-adapter-security.md`,
now observed holding on a third, structurally different adapter (binary
RPC vs. REST-ish OData vs. wire-protocol pgwire). No new doc needed for this
half — treat it as reinforcement of the existing steering doc/rule, not a
separate lesson.

## Scope

Applies to any future `IProtocolAdapter` that (a) derives its schema/service
surface dynamically from `DbModel` rather than from a fixed IDL, or (b) has
its hosting slice land before its auth slice in a multi-slice build.
