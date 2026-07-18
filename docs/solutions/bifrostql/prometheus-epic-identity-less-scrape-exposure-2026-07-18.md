---
written_at: 2026-07-18T21:10:00Z
source_event: epic:01KWXCZ1QW2TW4Z4MFKYXE0BTN
module: bifrostql
category: security-architecture
confidence: high
sources:
  - epic:01KWXCZ1QW2TW4Z4MFKYXE0BTN
  - task:01KXM41EB6ZNYKRQNVZ4MDZBP6
  - task:01KXM41EC5TVA0J1SA95YCNT7Z
  - task:01KXM41ED6Y2SNBGQE6BRQY6YZ
  - workflow-step:01KXM42E4HH2EJ98A5PD7TS72Y
tags: [protocol-adapter, prometheus, identity, tenant-isolation, cache, metrics, cardinality]
status: steering
recurrence: 1
---

# Prometheus epic: identity-less scrape surfaces need an explicit cross-tenant-exposure decision

**Lesson.** Every other Bifrost front door (GraphQL, OData, gRPC, pgwire,
RESP, S3, MCP) carries a per-request caller identity that
`IBifrostAuthContextFactory` and the mutation/query pipelines scope against.
Prometheus's `/metrics` scrape is the first surface where the CLIENT (a
scrape target, not a user) has no per-request identity at all, yet the data
it serves is an AGGREGATE computed over tenant-partitioned rows. "No
identity" cannot mean "no scoping" — an ambient/anonymous aggregate query is
a structural cross-tenant leak by construction, not a bug to catch later.

**The resolution (generalize this to any future identity-less pull/push
surface — a webhook emitter, a scheduled export, an OTLP/OpenMetrics
endpoint, a data-warehouse sync):**

Cross-tenant aggregate exposure must be an EXPLICIT DEPLOYMENT DECISION,
resolved into exactly one of two modes, never left implicit:

1. **Fixed service identity** — the deployment configures one
   `IBifrostAuthContextFactory`-projected identity that the scrape runs as;
   every business series is scoped to that identity's tenant/visibility,
   same as any other caller.
2. **Declared tenant-label partitioning** — the metric's GROUP BY includes
   the schema-declared tenant column as a label, so the aggregate is
   partitioned per tenant in the output rather than collapsed across
   tenants.

Every misconfiguration (no fixed identity configured, no tenant label
declared, credential absent/wrong) must fail closed to **no metric at all**
— never a silent ambient/global aggregate. The epic wired this as: business
metrics default OFF, credential gate is the FIRST check in the handler,
constant-time `FixedTimeEquals` runs unconditionally against a decoy digest
when disarmed (absent ≡ wrong ≡ disabled, uniform 401 — same anti-oracle
shape as the pgwire auth invariant), and there is no code path that emits a
business series without one of the two modes resolved.

**Two reusable findings folded into the same epic:**

- **Cache key must include the identity partition.** A cached aggregate
  keyed WITHOUT the resolved identity/tenant partition is a cross-tenant
  CACHE leak even when the underlying query path is correctly scoped — the
  first scrape computes tenant A's number, a differently-scoped second
  scrape reads it back from cache. Any caching layer sitting in front of
  identity-scoped data must partition its key by that identity, not just by
  the query shape.
- **Structurally-bounded metric labels.** Engine self-metrics (request
  counts, SQL latency, transformer timings) use a bounded-enum label API —
  table name, tenant, user, exception text are UNREPRESENTABLE as a label
  at compile time, not merely discouraged by convention. This closes both
  an unbounded-cardinality DoS vector and an info-disclosure vector at the
  type level. Reusable for any metrics/telemetry surface: prefer an enum
  record API over free-text labels whenever the label set can be enumerated
  ahead of time.

**Why durable:** this is the first identity-less adapter Bifrost has built;
no existing protocol-adapter-security rule covers "the caller has no
identity at all." The two-mode resolution (fixed service identity vs.
declared tenant-label partitioning, fail-closed on neither) is the reusable
shape for every future push/pull surface without per-request auth.

**Promotion recommendation:** worth lifting to
`.claude/rules/protocol-adapter-security.md` as a new numbered invariant —
it is a cross-adapter security pattern in the same vein as invariants 1-9,
just triggered by "no per-request identity" rather than "malformed input" or
"write routing." Left as steering-only here per single-epic-occurrence
recurrence gate; promote on the next identity-less surface (or on request).

---

## Process note (loop-ops, not a bifrostql lesson)

This epic hit two operational events worth a one-line note for whoever
tunes worktrack-loop, not for this steering doc's audience:

1. A mid-epic FALSE-PREMISE task: slice 3 (scrape security gate) was marked
   HIGH priority while its prerequisites (slices 1-2, metadata contract +
   aggregate planner) were still unbuilt, making it claimable out of
   dependency order via priority alone. Released unworked, reprioritized,
   epic rebuilt in dependency order (see the epic's `## Delivery plan`
   with explicit `Dependencies` notes per child — added specifically to
   prevent recurrence).
2. A subagent died mid-task (session limit) leaving uncommitted wiring; a
   fresh agent verified the partial state and finished it rather than
   redoing it blind.

Neither is bifrostql-specific; if not already covered by an existing
worktrack-loop operations doc, consider folding into loop dependency-order
validation (gate priority against unmet prerequisite tags at claim time)
and the subagent-resume-on-death pattern.
