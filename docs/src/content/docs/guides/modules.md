---
title: Module System
description: Cross-cutting concerns via filter transformers, mutation transformers, and query observers.
---

BifrostQL's module system handles cross-cutting concerns through metadata configuration. No custom code required for common patterns like tenant isolation, claim-based filters, soft-delete, and audit columns.

Need to plug in your *own* logic — a custom filter, a mutation veto, an async validator, a computed column? See [Extending BifrostQL (Hooks & Providers)](/BifrostQL/guides/extensibility/) for the programming surface.

## Architecture

The module system has these extension points:

| Extension | Interface | Purpose | Behavior |
|-----------|-----------|---------|----------|
| **Filter Transformer** | `IFilterTransformer` | Inject WHERE clauses | Applied to every SELECT. Throws to abort queries. |
| **Mutation Transformer** | `IMutationTransformer` | Transform mutation operations | Can change operation type (e.g., DELETE → UPDATE). Runs `TransformAsync`. |
| **Before-Commit Hook** | `IBeforeCommitMutationHook` | Veto a mutation inside the transaction | Returns errors (or throws) to roll back before commit. |
| **Query Observer** | `IQueryObserver` | Side effects at lifecycle phases | Exceptions are logged but don't abort queries. |
| **Server Validator** | `IServerValidationProvider` | Async per-mutation validation | Returns error messages; any error aborts the mutation. |
| **Computed Column** | `IComputedColumnProvider` | Provider-backed virtual fields | Computed after the row is read. |

The built-in transformers (tenant filter, auto-filter, soft-delete, audit columns, policy, state machine, enum value, server validation) **auto-register from metadata** — declaring the metadata key is enough; you do not wire them up in code. See [Extending BifrostQL](/BifrostQL/guides/extensibility/) to register your own.

## Tenant isolation

The tenant filter transformer injects a WHERE clause on every query to a configured table, using the authenticated user's tenant ID.

### Configuration

```json
{
  "BifrostQL": {
    "Metadata": [
      "dbo.orders { tenant-filter: tenant_id; }",
      "dbo.products { tenant-filter: tenant_id; }",
      "dbo.invoices { tenant-filter: tenant_id; }"
    ]
  }
}
```

Or apply to all tables that have a `tenant_id` column:

```
"dbo.*|has(tenant_id) { tenant-filter: tenant_id; }"
```

### How it works

1. BifrostQL reads `tenant_id` from the authenticated user's JWT claims via `BifrostContext`
2. Every query to a tenant-filtered table gets `WHERE tenant_id = @tenantId` appended
3. If the tenant ID is missing from the user context, the query is aborted with an error

There is no opt-out per query. The filter is applied at the SQL level before execution. Queries physically cannot return data from another tenant.

### Configuring the claim key

By default, BifrostQL looks for `tenant_id` in the user context. Change the claim key with model-level metadata:

```
":root { tenant-context-key: org_id; }"
```

## Automatic claim filters

Use `auto-filter` when the filter is not strictly a tenant ID, or when one table needs several claim-to-column mappings.

```json
{
  "BifrostQL": {
    "Metadata": [
      "dbo.orders { auto-filter: organization_id:org_id,region_id:region; }",
      ":root { auto-filter-bypass-role: admin; }"
    ]
  }
}
```

Each mapping is `column:claim`. The transformer reads the claim from the authenticated user context and injects an equality filter for that column. If the claim value is an array, the generated SQL uses an `IN` filter. The bypass role is read from `UserContext["roles"]`.

## Soft delete

Soft delete converts DELETE mutations into UPDATE mutations that set a timestamp, and adds a filter to all SELECT queries to exclude soft-deleted rows.

### Configuration

```json
{
  "BifrostQL": {
    "Metadata": [
      "dbo.orders { soft-delete: deleted_at; soft-delete-by: deleted_by_user_id; delete-type: soft; }"
    ]
  }
}
```

### What changes

- **Queries**: A `WHERE deleted_at IS NULL` filter is added to every query against the table
- **DELETE mutations**: Transformed into `UPDATE ... SET deleted_at = NOW(), deleted_by_user_id = @userId`
- **Schema**: Soft-deleted tables expose extra query and mutation arguments for administrative access

| Argument | Side | Effect |
|----------|------|--------|
| `_includeDeleted: Boolean` | query | Include soft-deleted rows alongside live rows |
| `_onlyDeleted: Boolean` | query | Return **only** soft-deleted rows (takes precedence) — power a "recycle bin" view |
| `_hardDelete: Boolean` | mutation | Bypass the soft-delete rewrite and issue a real `DELETE` |

```graphql
# Administrative read — show the recycle bin
{
  orders(_onlyDeleted: true) {
    data { orderId deletedAt }
  }
}
```

```graphql
# Permanently remove a row (bypasses soft-delete)
mutation {
  orders(delete: { orderId: 42 }, _hardDelete: true) { orderId }
}
```

The standard delete mutation shape is unchanged: `orders(delete: { orderId: 42 })` soft-deletes.

### Priority ordering

Filter transformers run in priority order (lower number = applied first). The built-in priorities:

- **0-99**: Security and tenant isolation
- **100-199**: Data filtering (soft-delete)
- **200+**: Application-level filters

This means tenant filters are always applied inside (closer to the query), and soft-delete filters wrap around them.

## Audit columns

The audit module auto-populates columns from the authenticated user context during mutations.

### Configuration

```json
{
  "BifrostQL": {
    "Metadata": [
      "dbo.*.createdOn { populate: created-on; update: none; }",
      "dbo.*.updatedOn { populate: updated-on; update: none; }",
      "dbo.*.createdBy { populate: created-by; update: none; }",
      "dbo.*.updatedBy { populate: updated-by; update: none; }"
    ]
  }
}
```

### Populate values

| Value | Populated with |
|-------|---------------|
| `created-on` | Current timestamp (on insert only) |
| `updated-on` | Current timestamp (on insert and update) |
| `created-by` | User audit key from context (on insert only) |
| `updated-by` | User audit key from context (on insert and update) |
| `deleted-on` | Current timestamp (on soft-delete) |
| `deleted-by` | User audit key from context (on soft-delete) |

The `update: none` setting removes these columns from the GraphQL input types so consumers cannot set them manually.

## Query observers

Observers receive notifications at four lifecycle phases:

1. **Parsed** -- after the GraphQL query is parsed into the internal query tree
2. **Transformed** -- after filter and mutation transformers have run
3. **BeforeExecute** -- immediately before SQL execution
4. **AfterExecute** -- after SQL execution completes

Observers are for side effects: logging, metrics, auditing. If an observer throws, the exception is logged and the query continues. Observers do not modify queries.

## Visibility control

Hide tables and columns from the GraphQL schema entirely:

```
"dbo.sys* { visibility: hidden; }"
"dbo.*.__* { visibility: hidden; }"
"dbo.*.password_hash { visibility: hidden; }"
"dbo.*.internal_notes { visibility: hidden; }"
```

Hidden items are excluded from the generated schema. They don't appear in queries, mutations, or introspection results.
