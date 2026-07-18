---
title: "Prometheus Metrics Endpoint"
description: "Expose declared per-table business metrics — plus BifrostQL's own engine self-metrics — over a Prometheus /metrics scrape endpoint. Covers the metric metadata (name/help/count/sum/labels/max-cardinality/security-mode), the credential gate and aggregate vs per-tenant scoping, the single-flight cache, cardinality and timeout bounds, the bifrostql_engine_* self-metric namespace, AddBifrostPrometheus/UseBifrostPrometheus registration, and the honest non-goals — every aggregate routed through the same transformer pipeline as GraphQL."
---

BifrostQL can expose your tables as **Prometheus metrics** over an opt-in
`/metrics` scrape endpoint. You mark a table with metric metadata — a name, a
count and/or sum source, an optional label set — and each scrape runs a grouped
aggregate over that table and renders the result as Prometheus `0.0.4` text.

It is a [protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is
the Prometheus exposition format, but every aggregate still executes through the
same transformer pipeline as GraphQL, so **tenant isolation, soft-delete
invisibility, and policy read guards scope the numbers a scrape can see** — the
adapter builds no predicate of its own.

A Prometheus scrape carries no per-user identity, so this endpoint is
**deliberately conservative**. Business metrics default **off**, the endpoint is
gated by a shared credential, and a tenant-scoped table must make an **explicit**
decision about how its tenant dimension is exposed before any series is emitted.

## Enabling the endpoint

Register the surface with `AddBifrostPrometheus`, then mount it with
`UseBifrostPrometheus`. Both are opt-in and inert when not configured (mirroring
the OData, gRPC, pgwire, RESP, and S3 adapters), so a host may call
`UseBifrostPrometheus` unconditionally.

```csharp
builder.Services.AddBifrostQL(o => o
    // ... your database + module setup ...
);

builder.Services.AddBifrostPrometheus(p =>
{
    // Security — the credential gate + scoping authority.
    p.Security = new PrometheusScrapeSecurityOptions
    {
        BusinessMetricsEnabled = true,          // default false — nothing is exported until you opt in
        ScrapeCredential = "<shared-secret>",   // default null — the bearer secret a scraper presents
        // ServiceIdentity = <ClaimsPrincipal>, // the fixed principal a tenant-scoped aggregate runs under
    };

    // Exposition — route, backing endpoint, cache, cardinality, request bounds.
    p.Exposition = new PrometheusExpositionOptions
    {
        RoutePath = "/metrics",                 // default /metrics
        Endpoint = "/graphql",                  // which registered BifrostQL endpoint backs the metrics
        CacheTtl = TimeSpan.FromSeconds(10),    // single-flight cache freshness window
        GlobalMaxCardinality = 1000,            // backstop on label-value series per metric; null disables
        MaxRequestBodyBytes = 0,                // a scrape is a body-less GET
    };

    // Collection — per-metric query bound.
    p.Collection = new PrometheusCollectionOptions
    {
        QueryTimeout = TimeSpan.FromSeconds(10),
    };
});

var app = builder.Build();

app.UseBifrostPrometheus();   // mounts /metrics on its own branch; inert if not registered
app.UseBifrostEndpoints();
app.Run();
```

Arming the surface (both `BusinessMetricsEnabled` **and** a `ScrapeCredential`
set — the `IsArmed` condition) logs a startup posture warning, since it is a
deployment-visible change. A surface that is enabled but has no credential (or
vice versa) is **disarmed**: the gate denies every scrape uniformly.

## Declaring a metric

A table opts into the metric surface by carrying `metric-name`. Configure it the
same way as the tenant-filter convention — table-level metadata:

```csharp
"dbo.orders { metric-name: orders_total; metric-help: Orders placed;
              metric-count: enabled; metric-sum: Total; metric-labels: Status;
              metric-security-mode: per-tenant }"
```

The recognized keys (all in `MetadataKeys.Metrics`):

| Key | Meaning |
|-----|---------|
| `metric-name` | The exported series name. **Its presence is what opts a table in.** Must match the Prometheus metric-name grammar `[a-zA-Z_:][a-zA-Z0-9_:]*`. |
| `metric-help` | Human-readable `HELP` text. Optional, but a present-but-empty value is rejected. |
| `metric-count` | Count source. The token `enabled` counts every row (`COUNT(*)`); any other value names a column whose non-null values are counted (`COUNT(column)`). |
| `metric-sum` | Names a numeric column summed into the metric (`SUM(column)`). |
| `metric-labels` | Comma-separated label columns. Each must exist and must **not** be field-encrypted (a label is cleartext on the wire). Names that normalize to the same exported label are a rejected collision. |
| `metric-max-cardinality` | Optional positive-integer cap on the distinct label-value series this metric may emit before collection caps it. |
| `metric-security-mode` | Explicit scrape-security mode, **required** when the table also carries `tenant-filter`. One of `aggregate` or `per-tenant`. |

A metric must declare a **count and/or a sum** source. The count is exported
under the metric name; the sum is exported under a `<name>_sum`-suffixed series,
so the two projections never collide. Both are rendered as Prometheus **gauges**
— a scrape-time aggregate snapshot, not a monotonic process counter.

Validation is fail-fast at model load: a non-numeric sum column, an encrypted
label, a colliding label, a non-positive cardinality, or a tenant-scoped table
with no `metric-security-mode` is rejected before the endpoint ever serves.

## Credential and security modes

The credential gate is the **first** check in the middleware — before any method
or body parsing, model lookup, collection, or cache read. A denied scrape builds
zero intent and receives one **uniform 401** (`WWW-Authenticate: Bearer`, a
sanitized `# unauthorized` body) whether the credential is **absent**, **wrong**,
or the surface is **disarmed** — no oracle distinguishes them. The scraper
presents the secret as `Authorization: Bearer <ScrapeCredential>`.

Once past the gate, each metric's aggregate is scoped by its
`metric-security-mode`, resolved fail-closed:

- **Non-tenant table** (no `tenant-filter`) — no tenant dimension to scope. The
  aggregate runs under an empty context, exactly as a non-tenant row read does.
- **`aggregate`** — the aggregate runs under the fixed
  `PrometheusScrapeSecurityOptions.ServiceIdentity`, projected through the shared
  [`IBifrostAuthContextFactory`](/BifrostQL/concepts/protocol-adapters/). That
  identity is the scoping authority: whatever *it* can see (its tenant, its
  policy grants) is what the metric exposes. This deliberately exports a
  single-tenant slice under a service principal — the operator acknowledges the
  exposure by choosing this mode.
- **`per-tenant`** — same fixed service identity, but the table is **rejected
  unless its tenant column is a declared `metric-labels` column**. The tenant
  column being a label is what makes every emitted series carry its tenant
  dimension, so a scraper can never read one tenant's aggregate as an
  un-partitioned global total.

Every one of these is fail-closed: a tenant-scoped metric with **no configured
service identity**, no declared mode, or (per-tenant) a non-partitionable table
is **excluded** — the aggregate never runs under an anonymous/global context.
Exclusions are logged server-side and never surface their reason on the wire.

Because each aggregate crosses `IQueryIntentExecutor`, the tenant-filter,
soft-delete, and policy transformers scope it exactly as they scope a row read,
and the aggregate SQL is **parameterized** — the identity value is bound, never
concatenated into SQL.

## Cache, cardinality, and timeout behavior

- **Single-flight cache.** A successfully collected series stays fresh for
  `Exposition.CacheTtl` (default **10s**). Within the TTL, concurrent scrapes are
  served the cached series and collapse to **one aggregate query per series** —
  a scrape storm cannot fan out to N queries. The cache key partitions by
  endpoint, model version, series identity, security mode, and an
  identity-partition fingerprint, so one identity's cached series is never served
  to another.
- **Cardinality guard.** A metric emitting more label-value series than its
  effective cap — its own `metric-max-cardinality`, else
  `Exposition.GlobalMaxCardinality` (default **1000**) — is **capped**: the
  series is not emitted and the operator sees a `bifrostql_prometheus_metric_capped`
  health metric. A labeled metric with **no** per-metric cap **and**
  `GlobalMaxCardinality = null` is an unbounded configuration, rejected as a
  scrape-error rather than emitted.
- **Query timeout.** A collection that exceeds `Collection.QueryTimeout` (default
  **10s**) is cancelled cleanly and surfaced as a scrape-error health metric —
  never a partial or silently-dropped series.
- **Failure isolation and recovery.** A per-metric collection failure is
  sanitized (logged server-side only, **never** forwarded verbatim onto the wire)
  and surfaced as `bifrostql_prometheus_scrape_error`; it never fails the whole
  scrape. A failure is not cached as fresh, so a subsequent scrape re-collects
  and recovers. If a prior success is still cached it is served **stale** and
  flagged with `bifrostql_prometheus_metric_stale`.

### Scrape health self-metrics

Every scrape emits these operator-facing gauges, each labeled `metric="<name>"`,
independent of business-series success:

| Metric | Meaning |
|--------|---------|
| `bifrostql_prometheus_scrape_success` | 1 if the metric's series was collected on this scrape, else 0. |
| `bifrostql_prometheus_scrape_error` | 1 if the metric's collection failed on this scrape. |
| `bifrostql_prometheus_metric_capped` | 1 if the metric was rejected for exceeding its cardinality cap. |
| `bifrostql_prometheus_metric_stale` | 1 if a cached (stale) series was served because this scrape's collection failed. |
| `bifrostql_prometheus_last_success_timestamp_seconds` | Unix seconds of the metric's most recent successful collection. |

## Engine self-metrics

When the scrape surface is configured, BifrostQL also exports its **own** engine
health, rendered **separately** from the database-derived business series and
carrying the `bifrostql_engine_` prefix. Only bounded enum labels ever reach the
wire (`operation`, `outcome`, `adapter`), so the label sets are bounded by
construction. The scrape's own collection queries are excluded from these
instruments, so serving a scrape never measures itself.

| Metric | Type | Labels |
|--------|------|--------|
| `bifrostql_engine_requests_total` | counter | `operation` (read/write), `outcome` (success/error/denied) |
| `bifrostql_engine_sql_duration_seconds` | histogram | `operation` |
| `bifrostql_engine_transformer_duration_seconds` | histogram | `operation` |
| `bifrostql_engine_active_connections` | gauge | `adapter` |

## Scraping it

Point Prometheus at the endpoint with a bearer credential. The endpoint answers
`text/plain; version=0.0.4; charset=utf-8`.

```yaml
scrape_configs:
  - job_name: bifrostql
    metrics_path: /metrics
    scheme: https
    authorization:
      type: Bearer
      credentials: "<scrape-credential>"
    static_configs:
      - targets: ["bifrostql.internal:443"]
```

A pinned copy of this scrape config and an example Grafana dashboard (charting a
metadata-marked business metric plus an engine self-metric and the scrape-health
gauge) live under the integration tests at
`tests/BifrostQL.Integration.Test/Prometheus/fixtures/`. They are documentation
examples, not a live test dependency — the normal suite validates the exposition
output against the `0.0.4` grammar directly, without standing up a real
Prometheus or Grafana.

## Non-goals

This endpoint is a scrape surface, not a full metrics platform. It deliberately
does **not** provide:

- **No pushgateway / push model** — scrape-only, pull-based.
- **No remote-write** — BifrostQL exposes `/metrics`; it does not write to a
  remote Prometheus.
- **No alert rules or recording rules** — define those in Prometheus itself.
- **No per-user metrics endpoint** — a scrape carries no per-user identity;
  tenant-scoped exposure is an explicit `aggregate` / `per-tenant` decision.
- **No OpenMetrics negotiation** — the endpoint emits the `0.0.4` text format
  only.
