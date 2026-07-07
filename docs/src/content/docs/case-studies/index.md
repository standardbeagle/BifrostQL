---
title: Case Studies Overview
description: Simulated, end-to-end walkthroughs of real deployment shapes — legacy desktop apps, two-tier admin portals, and multi-tenant SaaS back offices.
---

The guides section documents each BifrostQL feature in isolation. This section does the opposite: each page here takes a **realistic, simulated scenario** — a company, a legacy database, a set of constraints — and walks it end to end, showing how the features compose into a shipped system.

The companies and databases are invented, but every configuration block, metadata rule, query, and component in these pages is real and runnable against the current release.

## The case studies

### [Adding a web admin to an existing WPF LOB app](/BifrostQL/case-studies/wpf-lob-admin/)

*Meridian Supply Co.* has a ten-year-old WPF order-management app over SQL Server. The desktop app stays exactly as it is; BifrostQL puts a browser-based admin surface next to it — schema-driven UI, audit columns, and hidden internals — without a single stored procedure or migration.

**Features exercised:** metadata rules, `visibility: hidden`, audit `populate` columns, the embeddable `@standardbeagle/edit-db` editor, local-user auth.

### [Two admin sections: application API vs. raw SQL](/BifrostQL/case-studies/two-tier-admin/)

*Brightline Bookings* runs one admin portal with two very different rooms in it. Support staff get a **curated admin** built on the application-shaped GraphQL API — soft deletes, guarded mutations, tenant rules all enforced server-side. The two on-call engineers get an **ops console** with the raw SQL escape hatch — role-gated, row-capped, and time-boxed.

**Features exercised:** `_rawQuery` and `raw-sql-*` metadata, roles, soft delete, mutation guardrails, custom fetcher auth, building both surfaces in one React app.

### [Multi-tenant field-service SaaS back office](/BifrostQL/case-studies/multi-tenant-saas/)

*Fieldstone* sells field-service management to landscaping companies. Every customer's data lives in one database, separated by a `tenant_id` column. This walkthrough builds the customer-facing back office where tenant isolation, soft delete, and audit columns are enforced by the server — the client code never mentions a tenant.

**Features exercised:** `tenant-filter`, `auto-filter` with bypass roles, soft delete with `deleted-by`, local auth with tenant + roles columns, per-table page-size defaults.

## How to read these

Each case study follows the same arc:

1. **The situation** — the fictional company, its database, and its constraints.
2. **The plan** — which BifrostQL features map to which constraint.
3. **The walkthrough** — server setup, metadata configuration, and client code, in the order you'd actually build it.
4. **What we didn't have to build** — a checklist of the code the approach avoided.

If a step uses a feature you haven't met, each page links back to the relevant guide the first time it appears.
