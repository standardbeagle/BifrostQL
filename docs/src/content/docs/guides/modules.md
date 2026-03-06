---
title: Module System
description: Cross-cutting concerns via filter transformers, mutation transformers, and query observers.
---

BifrostQL's module system handles cross-cutting concerns through metadata configuration. No custom code required for common patterns like tenant isolation, soft-delete, and audit columns.

## Architecture

The module system has four extension points:

| Extension | Purpose | Behavior |
|-----------|---------|----------|
| **Filter Transformer** | Inject WHERE clauses | Applied to every SELECT. Throws to abort queries. |
| **Mutation Transformer** | Transform mutation operations | Can change operation type (e.g., DELETE to UPDATE). |
| **Query Observer** | Side effects at lifecycle phases | Exceptions are logged but don't abort queries. |
| **Mutation Module** | Modify mutation data | Populate columns before insert/update. |

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

By default, BifrostQL looks for `tenant_id` in the user context. Change the claim key with:

```
"dbo.* { tenant-context-key: org_id; }"
```

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
- **Schema**: No visible change -- the delete mutation still looks like `delete_orders(filter: ...)` to the consumer

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
