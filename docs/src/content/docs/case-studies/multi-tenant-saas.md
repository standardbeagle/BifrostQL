---
title: "Case Study: Multi-Tenant SaaS Back Office"
description: A simulated walkthrough of a field-service SaaS where tenant isolation, soft delete, and audit are enforced entirely server-side — client code never mentions a tenant.
---

:::note[Simulated case study]
Fieldstone is fictional; the tenant-isolation configuration is real. This walkthrough shows the pattern behind most multi-tenant BifrostQL deployments: one database, a `tenant_id` column, and a server that makes cross-tenant access structurally impossible rather than merely discouraged.
:::

## The situation

Fieldstone sells field-service management to landscaping companies: crews, jobs, schedules, invoices. Classic B2B SaaS shape:

- **One PostgreSQL database** for all customers. Every tenant-owned table has a `tenant_id` column.
- Each customer gets a **back office** — their staff manage their own crews and jobs.
- Fieldstone's own support team occasionally needs **cross-tenant** visibility to debug.

```
public.crews      (crew_id PK, tenant_id, name, ...)
public.jobs       (job_id PK, tenant_id, crew_id FK, customer_name, status,
                   scheduled_on, deleted_at, deleted_by, created_*, updated_*)
public.invoices   (invoice_id PK, tenant_id, job_id FK, total, status, ...)
public.app_users  (id PK, email, password_hash, display_name, tenant_id, roles)
public.plans      (plan_id PK, name, ...)        -- global, not tenant-owned
```

**Constraints:**

1. A tenant's user must never see — or write — another tenant's rows. Ever. Including via filters, joins, and mutations.
2. Application developers must not be able to *forget* the tenant clause. History says someone always forgets one `WHERE`.
3. Fieldstone support can see across tenants, but that must be an explicit role, not the default.
4. Deleting a job archives it (soft delete) with attribution.

## The plan

The whole strategy is: **the client never sends a tenant ID, and the server never trusts one.** The tenant comes from the authenticated identity, and a server-side transformer injects it into every query and mutation on every tenant-owned table.

| Constraint | BifrostQL feature |
|------------|-------------------|
| Isolation on every query/join/mutation | `tenant-filter` [metadata](/BifrostQL/reference/configuration/) → `TenantFilterTransformer` (priority range 0–99: security transformers run before everything else) |
| Impossible to forget | Selector `public.*\|has(tenant_id)` applies the rule to every table that has the column — including tables added next year |
| Support bypass | `auto-filter` + `auto-filter-bypass-role` |
| Soft delete with attribution | `delete-type: soft`, `soft-delete-by` |

## Walkthrough

### 1. Identity carries the tenant

Users log in with [local auth](/BifrostQL/guides/authentication/); the `app_users` table already has `tenant_id` and `roles` columns, which match `AddBifrostLocalAuth`'s defaults:

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBifrostLocalAuth(
    builder.Configuration.GetConnectionString("bifrost")!);

builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseAuthentication();
app.UseBifrostLocalAuth();
app.UseBifrostQL();
await app.RunAsync();
```

A successful login produces an `AppIdentity` whose `TenantId` comes from the user's row. `IdentityContextMapper` writes it into the user context under the `tenant_id` key — which is exactly what the tenant filter reads. If Fieldstone later moves to OIDC (say, Microsoft 365 with `tid` → `TenantId`), nothing below changes: the transformer reads the mapped identity, not the provider.

### 2. Isolation as configuration

```json
{
  "ConnectionStrings": {
    "bifrost": "Host=db;Port=5432;Database=fieldstone;Username=fieldstone_api;Password=xxx"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Provider": "postgres",
    "Metadata": [
      "public.*|has(tenant_id) { tenant-filter: tenant_id; }",

      "public.jobs { delete-type: soft; soft-delete: deleted_at; soft-delete-by: deleted_by; }",

      "public.*.created_on { populate: created-on; update: none; }",
      "public.*.created_by { populate: created-by; update: none; }",
      "public.*.updated_on { populate: updated-on; update: none; }",
      "public.*.updated_by { populate: updated-by; update: none; }",

      "public.crews { label: name; }",

      ":root { raw-sql: disabled; generic-table: disabled; default-limit: 50; }"
    ]
  }
}
```

The first rule is the one that matters:

```
public.*|has(tenant_id) { tenant-filter: tenant_id; }
```

- The `|has(tenant_id)` selector targets **every table that has the column** — `crews`, `jobs`, `invoices` today, and any tenant-owned table added later. A new table with a `tenant_id` column is isolated on the day it's created, with no config change. A developer cannot forget the tenant clause, because no developer writes it.
- `plans` and other global tables don't have the column, so they're untouched — every tenant can read the plan catalog.
- The filter applies to **reads, joins, and writes**. A query for `jobs` gets the tenant predicate; a join from `jobs` to `crews` gets it on both sides; an update or delete can only hit rows in the caller's tenant. Inserts are stamped with the caller's tenant.

Because the tenant filter is a security-priority module (0–99), it runs before any data-filtering or app-level transformers — nothing downstream sees un-scoped rows.

### 3. The back office never mentions tenants

The client is the [embeddable editor](/BifrostQL/guides/embedded-editor/) plus bespoke screens, and this is the entire point: **the client codebase contains no tenant logic whatsoever.**

```graphql
{
  jobs(filter: { status: { _eq: "scheduled" } }, sort: [scheduled_on_asc], limit: 50) {
    data {
      jobId
      customerName
      scheduledOn
      crews { name }
    }
  }
}
```

Two users at two different landscaping companies run this identical query and get disjoint results. There is no `tenantId` variable to leak, log, or tamper with. A hostile user modifying requests in dev tools has nothing to modify — the tenant predicate is added server-side from the session.

Soft delete behaves the same way it did in the [two-tier admin case study](/BifrostQL/case-studies/two-tier-admin/): "delete" a job and the server stamps `deleted_at`/`deleted_by`; the row vanishes from every subsequent query.

### 4. Support access is an explicit exception

Fieldstone's support staff exist in `app_users` with **no tenant** (`tenant_id` NULL) and a distinct role. Cross-tenant visibility is configured with `auto-filter` and its bypass role rather than by weakening the tenant rule:

```json
"public.*|has(tenant_id) { auto-filter: tenant_id:tenant_id; }",
":root { auto-filter-bypass-role: fieldstone-support; }"
```

`auto-filter` injects a filter from a user-context claim (here: column `tenant_id` from claim `tenant_id`), and `auto-filter-bypass-role` names the one role that skips it. The important property: bypass is **allow-listed by role**, not inferred from a missing claim. A tenant user with a broken session doesn't silently become global — they get no rows, not all rows.

Support sees cross-tenant data through the same shaped API — soft-deleted rows still hidden, audit columns still stamped. If Fieldstone later wants a support-only investigation console, that's the [raw SQL pattern](/BifrostQL/case-studies/two-tier-admin/) with `raw-sql-role`, fenced the same way.

For richer sharing shapes — users belonging to multiple organizations, org hierarchies — see the [Multi-Tenant Org Model guide](/BifrostQL/guides/org-model/), which extends this pattern with `OrgIds`.

### 5. Testing the fence

Isolation rules deserve tests that try to break them. Fieldstone's integration suite logs in as tenant A and asserts three things against tenant B's seeded data:

1. **Read:** `jobs` returns no tenant-B rows even with `filter: { tenant_id: { _eq: "tenant-b" } }` — an explicit filter for another tenant intersects with the injected predicate and yields nothing, rather than overriding it.
2. **Join:** a join traversal from a tenant-A row can't surface tenant-B rows on the far side.
3. **Write:** an update targeting a tenant-B primary key affects zero rows.

These tests run against the real GraphQL endpoint, not the transformers in isolation — the fence is only as good as the whole pipeline.

## What we didn't have to build

- ❌ A tenant-scoping convention every query author must remember
- ❌ Repository-layer guards re-implemented per table
- ❌ Per-tenant databases or schemas (with their migration fan-out)
- ❌ A separate support/admin API — one endpoint, role-differentiated
- ❌ Client-side tenant plumbing (context, headers, route params)

## Where to go deeper

- [Multi-Tenant Org Model](/BifrostQL/guides/org-model/) — multi-org membership and richer sharing
- [Module System](/BifrostQL/guides/modules/) — transformer priorities and how security modules compose
- [Authentication](/BifrostQL/guides/authentication/) — the identity contract that feeds the tenant filter
- [Configuration reference](/BifrostQL/reference/configuration/) — `tenant-filter`, `auto-filter`, and friends
