---
title: Workflow Mutations & Audit Trail
description: A convention for adding higher-level operations beyond raw table CRUD — sidecar workflow endpoints under the shared policy engine, with a tenant-scoped audit log queryable through Bifrost.
---

BifrostQL generates one mutation field per table: `insert`, `update`, `upsert`, `delete`. That generated CRUD is the right tool for most writes. But some operations are not a single-row write — *renew a membership*, *check in an attendee*, *invite a user*, *record a payment*. These touch several rows, enforce business rules, and must leave an audit trail. This guide defines the convention for those **workflow mutations** and the **audit-log** that records them.

The convention has three parts:

1. **Sidecar workflow endpoints** — ASP.NET endpoints that orchestrate Bifrost mutations server-side.
2. **A shared policy engine** — workflow endpoints reuse the same authorization policy as raw CRUD; they do not reimplement permission checks.
3. **A tenant-scoped audit log** — an ordinary Bifrost table that records each workflow operation and is queryable and tenant-filtered like any other table.

## Worked-example files

Everything below is backed by real files in the repository:

| File | Purpose |
|------|---------|
| `src/BifrostQL.UI/Schemas/org-model.sql` | The DDL — includes the `audit_log` table (SQLite) |
| `src/BifrostQL.UI/Schemas/org-model-seed-sample.sql` | Sample seed data with `audit_log` rows and the recommended metadata in its header (SQLite) |
| `src/BifrostQL.UI/Schemas/org-model-postgres-seed-sample.sql` | Self-contained DDL + seed for PostgreSQL, including `audit_log` |
| `tests/BifrostQL.Core.Test/Integration/Modules/WorkflowAuditCompositionTests.cs` | Integration tests proving audit entries are queryable through Bifrost and filtered by tenant |

## Generated CRUD vs. workflow mutation

Use the **generated CRUD** mutation when:

- The operation is a single-row create, update, or delete.
- The only rules are tenant isolation, row-scope, and column-write policy — all of which the [policy engine](/BifrostQL/guides/modules) and `tenant-filter` already enforce on the mutation path.
- Audit columns (`created-by` / `updated-by` / `updated-on`) are enough of a trail. See the [Multi-Tenant Org Model](/BifrostQL/guides/org-model) guide for the audit-column recipe.

Use a **workflow mutation** when *any* of these is true:

- The operation spans **more than one row or table** — e.g. *renew membership* updates `organization_memberships.status` **and** inserts a `payments` row **and** writes an `audit_log` entry.
- It enforces a **business rule** that is not expressible as policy metadata — e.g. *check in attendee* must reject if the event has not started, or if the attendee is already checked in.
- It must produce an **audit-log entry** describing the operation as a named action (`membership.renewed`), not just a column diff.
- It needs to be **exposed as one atomic, named operation** to clients, so they cannot perform half of it.

The rule of thumb: **if you would have to explain the operation to a user in a sentence with a verb in it, it is a workflow mutation.** "Renew Carol's membership" is a workflow mutation; "set `organization_memberships.status` to `active`" is raw CRUD.

Workflow mutations do **not** replace generated CRUD — they sit alongside it. Generated CRUD remains available for the simple writes, and the workflow endpoint is itself implemented *in terms of* generated CRUD (it issues Bifrost mutations internally). This preserves Bifrost's zero-code CRUD value while giving apps a place for higher-level operations.

## The sidecar endpoint pattern

A workflow mutation is an **ASP.NET endpoint hosted alongside the Bifrost GraphQL endpoint** that orchestrates one or more Bifrost mutations server-side. It is a *sidecar*: it lives in the same host process, shares the same database connection and the same user context, but is a separate route from `/graphql`.

```csharp
// POST /workflows/membership/renew
// A sidecar workflow endpoint: one named operation, several Bifrost mutations.
app.MapPost("/workflows/membership/renew", async (
    RenewMembershipRequest request,
    HttpContext http,
    IBifrostWorkflowExecutor bifrost) =>
{
    // 1. Identity comes from the SAME user context the GraphQL endpoint uses.
    //    Do not re-derive it — reuse the request's resolved claims.
    var userContext = http.GetBifrostUserContext();

    // 2. Enforce business rules the policy engine cannot express.
    var membership = await bifrost.QuerySingleAsync(
        "organization_memberships", request.MembershipId, userContext);
    if (membership is null)
        return Results.NotFound();           // tenant-filter already scoped the read
    if (membership.Status == "active" && membership.ExpiresAt > DateTime.UtcNow.AddDays(30))
        return Results.BadRequest("Membership is not within its renewal window.");

    // 3. Orchestrate the generated CRUD mutations. Each call runs through the
    //    SAME mutation pipeline as a direct GraphQL mutation — tenant-filter,
    //    the policy engine, and the audit module all apply. No bypass.
    await bifrost.UpdateAsync("organization_memberships", new
    {
        membership_id = request.MembershipId,
        status = "active",
        expires_at = DateTime.UtcNow.AddYears(1),
    }, userContext);

    await bifrost.InsertAsync("payments", new
    {
        tenant_id = userContext["tenant_id"],
        membership_id = request.MembershipId,
        amount = request.Amount,
    }, userContext);

    // 4. Write the audit entry — see the next section.
    await bifrost.InsertAsync("audit_log", new
    {
        tenant_id = userContext["tenant_id"],
        actor_user_id = userContext["user_id"],
        action = "membership.renewed",
        entity_type = "organization_memberships",
        entity_id = request.MembershipId.ToString(),
        summary = $"Membership {request.MembershipId} renewed for one year",
    }, userContext);

    return Results.Ok();
});
```

Key properties of the pattern:

- **It runs server-side.** The endpoint is the only thing that knows how to compose the steps. A client cannot do half a renewal.
- **It reuses the Bifrost mutation pipeline.** Every `UpdateAsync` / `InsertAsync` call goes through the same `IMutationTransformer` chain as a direct GraphQL mutation, so `tenant-filter`, `PolicyMutationTransformer`, and `BasicAuditModule` all still apply. The workflow endpoint adds business logic *on top of* the pipeline; it never bypasses it.
- **It is named.** The route (`/workflows/membership/renew`) and the audit `action` (`membership.renewed`) name the operation, which is what makes the audit trail readable.

> **Why an endpoint and not a custom `IMutationTransformer`?** An `IMutationTransformer` transforms *one* mutation in flight — it is the right tool for cross-cutting rules applied to every mutation on a table (tenant scope, soft-delete, policy). A workflow mutation is the opposite shape: a *composite* of several mutations driven by procedural business logic. Forcing that into a transformer would mean a transformer triggering further mutations, which inverts the pipeline. A sidecar endpoint keeps the composition explicit and debuggable. Transformers stay for per-mutation cross-cutting concerns; endpoints own multi-step workflows.

## Shared permission checks

A workflow endpoint **must not reimplement authorization**. The repository already has a server-side policy engine — `PolicyEvaluator`, `PolicyFilterTransformer`, and `PolicyMutationTransformer` (`src/BifrostQL.Core/Auth/`, `src/BifrostQL.Core/Modules/`) — described in the [Module System](/BifrostQL/guides/modules) guide. Workflow endpoints share it two ways:

1. **Through the pipeline, automatically.** Because the endpoint issues its writes through the Bifrost mutation pipeline, `PolicyMutationTransformer` runs on every one of them. If the caller lacks `update` on `organization_memberships`, the `UpdateAsync` call inside the endpoint is rejected exactly as a direct GraphQL mutation would be. The endpoint gets policy enforcement for free on each step.
2. **For a pre-flight gate, explicitly.** When the endpoint wants to reject the *whole* operation up front (rather than failing on step 3 of 4 and leaving steps 1–2 applied), it calls the *same* `PolicyEvaluator` the transformers use:

   ```csharp
   // Pre-flight: reject the whole workflow before any write, using the SAME
   // evaluator and the SAME TablePolicy that the mutation pipeline uses.
   var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("organization_memberships"));
   var identity = userContext.ToAppIdentity();   // canonical user_id + roles claims
   if (!evaluator.CanAct(policy, PolicyAction.Update, identity).Allowed)
       return Results.Forbid();
   ```

   This is the same `TablePolicy` parsed by `PolicyConfigCollector` and the same `PolicyEvaluator` (including its admin-role bypass) that `PolicyMutationTransformer` delegates to. There is exactly one source of truth for "can this identity do this"; the workflow endpoint reads it, it does not redefine it.

Because both paths terminate in the same evaluator and the same policy metadata, a permission change is made in **one place** — the table's `policy-*` metadata — and it applies to raw CRUD and every workflow endpoint at once.

If the workflow needs an operation-level permission that no table policy expresses (e.g. a `members:manage` permission claim gating *who may renew*), check the caller's `permissions` claim in the endpoint. Keep table-row authorization in the policy engine; use the claim check only for the operation-level gate.

## The audit log

The audit trail is **an ordinary tenant-scoped Bifrost table** — `audit_log` in `org-model.sql`:

| Column | Purpose |
|--------|---------|
| `audit_id` | Primary key |
| `tenant_id` | Owning tenant — makes the row tenant-scoped |
| `actor_user_id` | Who performed the action (from the `user_id` claim) |
| `action` | The named operation: `membership.renewed`, `membership.role_changed`, `payment.recorded`, `invitation.sent` |
| `entity_type` | The table the action targeted |
| `entity_id` | The primary key of the affected row |
| `summary` | A human-readable description |
| `created_at` | When the action happened |

It is **append-only by convention**: workflow endpoints `INSERT` rows, nothing updates or deletes them.

### What to audit

Write an `audit_log` entry for the operations the task scope calls out:

- **Status changes** — `membership.status_changed`, `membership.renewed`, `attendee.checked_in`.
- **Payment edits** — `payment.recorded`, `payment.refunded`.
- **Role changes** — `membership.role_changed`.
- **Admin actions** — anything an elevated role does that a regular member cannot: `member.removed`, `tenant.settings_changed`.

A plain single-row CRUD edit that is already covered by audit *columns* (`updated_by` / `updated_at`) does not also need an `audit_log` row — reserve the audit log for named workflow operations and admin actions.

### Auditing is tenant-scoped, queryable, and needs no custom code

`audit_log` carries the **same metadata recipe as every other tenant-scoped org table**:

```
audit_log { tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
```

That single line is the whole integration. Because `audit_log` is a normal table with `tenant-filter` + `auto-filter`:

- **It is queryable through Bifrost** like any other table — `query { audit_log { action summary created_at } }` — with filtering, ordering, and joins (e.g. join `actor_user_id` to `app_users`).
- **It is filtered by tenant automatically.** `tenant-filter` constrains every read to the caller's `tenant_id`; `auto-filter` widens it to the caller's full `tenant_ids` membership set. A caller can never read another tenant's audit entries, and no custom resolver code is involved.

`WorkflowAuditCompositionTests` proves exactly this against the generated SQL: the audit columns are selected through the standard query path, every read is constrained to the caller's tenant, the two security transformers AND-compose into one `WHERE`, a multi-tenant actor gets an `IN` clause across their tenants, and a missing tenant context fails closed.

To make the audit log **read-only to clients** — so only server-side workflow endpoints can append entries — add the policy-engine metadata:

```
audit_log { policy-actions: read }
```

`PolicyMutationTransformer` then rejects any client `insert` / `update` / `delete` on `audit_log`, while the workflow endpoint's server-side `InsertAsync` still works (it carries the caller's identity, and `read`-only policies do not block the server orchestration path when it runs as the workflow). Reads remain available to every tenant member, scoped by `tenant-filter`.

## Putting it together

For a higher-level operation such as *renew membership*:

1. **Endpoint** — expose it as a named sidecar route (`POST /workflows/membership/renew`) hosted alongside `/graphql`.
2. **Permission** — pre-flight with the shared `PolicyEvaluator`; each internal mutation is also re-checked by `PolicyMutationTransformer` on the pipeline.
3. **Orchestration** — issue the generated-CRUD mutations server-side through the Bifrost mutation pipeline, never bypassing it.
4. **Audit** — insert one `audit_log` row naming the action, the actor, the entity, and a summary.
5. **Query** — the audit trail is read back through ordinary Bifrost queries, tenant-filtered by the same `tenant-filter` + `auto-filter` metadata as the rest of the org model.

No custom resolver code is needed for the audit trail, and the workflow endpoint is thin: business rules plus orchestration on top of the CRUD that Bifrost already generates.

See the [Module System](/BifrostQL/guides/modules) guide for the policy engine and `tenant-filter` reference, and the [Multi-Tenant Org Model](/BifrostQL/guides/org-model) guide for the audit-column recipe and the tenant-isolation metadata.
