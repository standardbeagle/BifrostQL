---
title: Multi-Tenant Org Model
description: A reusable tenant/organization data model — canonical claims, deployment models, metadata rules, and tenant-owned mutation behavior — adoptable with metadata alone, no custom resolver code.
---

BifrostQL ships a reusable multi-tenant organization model: a small set of tables (tenants, app users, memberships, roles, role permissions, invitations) plus a metadata recipe that enforces tenant isolation on reads and mutations. The model is adoptable with metadata alone — no custom resolver code — because tenant isolation is delivered by the built-in `tenant-filter` and `auto-filter` modules and the audit module.

This guide ties together the canonical claim set, the three deployment models, the metadata rules, and how tenant columns are populated and enforced on mutations.

## Worked-example files

Everything below is backed by real files in the repository:

| File | Purpose |
|------|---------|
| `src/BifrostQL.UI/Schemas/org-model.sql` | The DDL — tenants, roles, role_permissions, app_users, organization_memberships, invitations (SQLite) |
| `src/BifrostQL.UI/Schemas/org-model-seed-sample.sql` | DDL is loaded separately; this is sample seed data (SQLite) with the recommended metadata in its header |
| `src/BifrostQL.UI/Schemas/org-model-postgres-seed-sample.sql` | Self-contained DDL + seed for PostgreSQL |
| `tests/BifrostQL.Core.Test/Integration/Modules/OrgModelCompositionTests.cs` | Integration tests proving isolation is enforced by `tenant-filter` + `auto-filter` composition alone |

The seed-file header comments carry the recommended metadata configuration, and `OrgModelCompositionTests` builds the same model in code and asserts the generated SQL is correctly scoped — including tenant isolation, multi-tenant membership `IN` clauses, the admin bypass role, and fail-closed behavior when claims are missing.

## Canonical claims

Every authentication path (local login, OIDC, JWT) converges on the same provider-neutral identity, and `IdentityContextMapper` projects it into the `UserContext` dictionary that the security modules read. The canonical claim names are finalized in `MetadataKeys.Auth` (`src/BifrostQL.Core/Model/MetadataKeys.cs`):

| Claim | Default key | Carries | Read by |
|-------|-------------|---------|---------|
| Tenant ID | `tenant_id` | The caller's primary tenant identifier | `TenantFilterTransformer` |
| Tenant IDs | `tenant_ids` | Every organization/tenant the caller belongs to (plural) | `AutoFilterTransformer` |
| User ID | `user_id` | The explicit user identifier | Custom modules / app code |
| Roles | `roles` | The caller's roles | `AutoFilterTransformer` (bypass-role check) |
| Permissions | `permissions` | The caller's permissions (plural) | Custom modules / app code |
| Audit user key | `user-audit-key` (model metadata; default `id`) | Which claim populates created-by / updated-by / deleted-by | `BasicAuditModule` |

`tenant_id` is the singular primary tenant; `tenant_ids` is the full membership set. The audit user key is configurable model-level metadata, not a fixed claim name — it names which claim the audit module reads.

## Deployment models

The same tables and modules cover three deployment shapes. What differs is which metadata is applied, which claims the identity carries, and which tables are tenant-scoped.

### Single-tenant

One organization per deployment. There is no cross-tenant data to isolate.

- **Metadata:** none of the tenant metadata is required. Optionally keep `tenant-filter: tenant_id` if you want defense-in-depth against a misconfigured connection string, but it is not load-bearing.
- **Claims:** `tenant_id` is constant (or absent). `roles` and `permissions` still drive authorization.
- **Tables:** all tables behave as plain application tables. `tenants` holds a single row.

### Multi-tenant

Many organizations share one database; each user belongs to one or more organizations and must only ever see their own.

- **Metadata:** tenant-scoped tables (`app_users`, `organization_memberships`, `invitations`) carry both `tenant-filter: tenant_id` and `auto-filter: tenant_id:tenant_ids`. Global lookup tables (`roles`, `role_permissions`) carry neither.
- **Claims:** `tenant_id` pins the caller's active tenant; `tenant_ids` carries the full membership set so the auto-filter can widen reads to every org the caller actually belongs to.
- **Tables:** `tenants` is the organization row itself — it carries `auto-filter: tenant_id:tenant_ids` (filtering on its own primary key) but no `tenant-filter`.

### Club-per-tenant

A variant of multi-tenant where the "organization" *is* the primary entity users interact with — for example a club, a workspace, or a project. The mechanics are identical to multi-tenant; the distinction is that queries against the `tenants` table itself are first-class.

- **Metadata:** same as multi-tenant. The key detail is that `tenants` carries `auto-filter: tenant_id:tenant_ids` so a caller listing "clubs" only sees the clubs they belong to.
- **Claims:** same as multi-tenant.
- **Tables:** `tenants` is queried directly and constrained on its own `tenant_id`. `OrgModelCompositionTests.ClubPerTenantPath_ReturnsOnlyCallersClubRows` is the worked example.

## Metadata rules

### `tenant-filter` vs `auto-filter`

| | `tenant-filter` | `auto-filter` |
|---|-----------------|---------------|
| Syntax | `tenant-filter: <column>` | `auto-filter: <column>:<claim>[,<column>:<claim>...]` |
| Claim read | `tenant_id` (override with model metadata `tenant-context-key`) | the claim named in each `column:claim` mapping |
| Filter shape | equality on the column | equality for a scalar claim, `IN` for an array claim |
| Use it for | the single active-tenant constraint that applies to **every** request | membership-based widening (`tenant_ids`) and any other claim-to-column row filter |
| Priority | 0 | 1 |

Use `tenant-filter` for the hard active-tenant boundary. Use `auto-filter` when a caller legitimately spans multiple tenants (the `tenant_ids` membership set) or when you need additional claim-driven row filters. On a tenant-scoped table the two compose: `tenant-filter` (priority 0) and `auto-filter` (priority 1) `AND`-combine into a single `WHERE` clause for the same query — see `OrgModelCompositionTests.TenantPlusAutoFilter_ComposeIntoCombinedWhereForSameQuery`.

The mapping syntax is `column:claim`. The seed-file headers and `OrgModelCompositionTests` both use `auto-filter: tenant_id:tenant_ids` — the `tenant_id` column is constrained to the values in the `tenant_ids` claim.

### Marking global lookup tables

Tables that are shared across all tenants — `roles` and `role_permissions` in this model — are **un-scoped**: they carry neither `tenant-filter` nor `auto-filter`. Adding either would incorrectly hide global rows. `OrgModelCompositionTests.TenantFilter_DoesNotApplyToGlobalLookupTables` asserts no filter is injected on these tables even when a tenant context is present.

### Priority ranges

Filter modules run in priority order, lowest first:

- **0–99** — security (tenant isolation): `tenant-filter` is 0, `auto-filter` is 1
- **100–199** — data filtering: e.g. soft-delete
- **200+** — application-level filters

Security filters run innermost (closest to the query), so nothing downstream can widen them.

### Admin bypass

Model-level metadata `auto-filter-bypass-role: <role>` skips the `auto-filter` module for callers holding that role. Note that **only** `auto-filter` is bypassed — `tenant-filter` has no bypass, so tenant isolation still holds for a bypass-role caller. `OrgModelCompositionTests.AdminBypassRole_SkipsAutoFilter_ButTenantFilterStillApplies` is the worked example.

## Mutation behavior for tenant-owned rows

Tenant-owned inserts and updates are enforced by two mechanisms working together: `tenant-filter` scopes which rows a caller may modify, and the audit module populates ownership columns from the caller's identity.

### Updates and deletes

`tenant-filter` injects its `WHERE tenant_id = <caller's tenant>` constraint into update and delete statements exactly as it does for reads. A caller can therefore only update or delete rows that already belong to their tenant — an attempt to update a row in another tenant matches zero rows. No custom resolver code is needed; the same `tenant-filter: tenant_id` metadata that scopes reads also scopes mutations.

### Inserts

On insert there is no existing row to filter, so the tenant column must be set explicitly. Two patterns apply:

1. **Client-supplied `tenant_id`.** The GraphQL insert input includes the `tenant_id` column; the client passes the caller's tenant. This is the simplest pattern and is what the org-model seed data uses. Pair it with application-side validation that the supplied `tenant_id` matches the caller's claim, or restrict who can call the mutation.
2. **Audit-populated ownership columns.** The audit module (`BasicAuditModule`) auto-populates `created-by` / `updated-by` columns from the `user-audit-key` claim and timestamps from `created-on` / `updated-on`. Configure it with column metadata:

   ```
   ":root { user-audit-key: user_id }"
   "main.app_users.created_at  { populate: created-on; update: none; }"
   "main.app_users.created_by  { populate: created-by; update: none; }"
   "main.app_users.updated_at  { populate: updated-on; update: none; }"
   "main.app_users.updated_by  { populate: updated-by; update: none; }"
   ```

   The audit module overwrites any client-provided value for these columns, so a client cannot spoof ownership. `update: none` removes the columns from the GraphQL input types entirely, so consumers never set them by hand.

The audit module's `populate` values are the fixed audit set (`created-on`/`created-by`, `updated-on`/`updated-by`, `deleted-on`/`deleted-by`) — it does not auto-populate an arbitrary `tenant_id` column. Tenant ownership on insert therefore comes from pattern 1 (client supplies `tenant_id`, validated against the caller's claim) while audit columns track *who* created or last touched the row. On updates, `tenant-filter` guarantees the caller can only reach their own tenant's rows, and the audit module refreshes `updated-by` / `updated-on` from the caller's identity.

### Putting it together

For a tenant-owned table such as `app_users`:

- **Read:** `tenant-filter: tenant_id` + `auto-filter: tenant_id:tenant_ids` constrain every query to the caller's organizations.
- **Insert:** the client supplies `tenant_id` (validated against the caller's claim); audit columns are auto-populated from the identity.
- **Update / delete:** `tenant-filter` scopes the statement to the caller's tenant; the audit module refreshes `updated-by` / `updated-on`.

No custom resolver code is involved at any step — the behavior is entirely metadata-driven, and `OrgModelCompositionTests` proves the read-path isolation against the generated SQL.

## Adopting the model

1. Load `org-model.sql` (or the PostgreSQL seed sample, which is self-contained) into your database.
2. Apply the metadata from the seed-file header comments via the BifrostQL `Metadata` config section.
3. Ensure your authentication path produces `tenant_id`, `tenant_ids`, and `roles` claims — see the [Authentication](/BifrostQL/guides/authentication) guide.
4. Optionally add audit columns and the audit metadata for mutation ownership tracking.

See the [Module System](/BifrostQL/guides/modules) guide for the full `tenant-filter`, `auto-filter`, and audit module reference.
