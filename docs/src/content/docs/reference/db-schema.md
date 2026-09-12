---
title: _dbSchema Reference
description: The caller-projected schema introspection root field — tables, columns, allowedActions, and the grant catalogue.
---

`_dbSchema` is the schema-introspection root field the shipped editor renders from. Since
the authorization-policy engine shipped, it answers **per caller**: the model is projected
through the same evaluator the data path enforces before anything is described, so the
answer never names something the caller may not read.

```graphql
query {
  _dbSchema(graphQlName: "members") {
    graphQlName
    allowedActions
    columns { graphQlName readable writable }
  }
  _grants
  _policyGrants
}
```

## Per-caller projection

- A table the caller may not **read** is **absent** from the result — the same answer a
  non-existent table gets, so the filter is never an existence oracle.
- A column the caller may not read is absent from its table's `columns`. A foreign-key or
  many-to-many edge is published only when both end tables and every participating column
  are visible.
- `metadata` (table and column) is the **raw metadata bag — served to admin callers
  only**. Non-admin callers receive an empty list, because the bag contains the
  `policy-*` rules themselves. This is a breaking change for non-admin consumers that
  read `metadata`.

## Table fields (new)

| Field | Type | Meaning |
|-------|------|---------|
| `allowedActions` | `[String!]!` | Subset of `read, create, update, delete` the **caller** may perform, resolved via the policy evaluator. An action the policy's allow-list omits is absent for everyone, admins included. |
| `isEditable` | `Boolean!` | Compatibility field: means exactly **"the table has a key"**. It is not an authorization answer — read `allowedActions` instead. |
| `metadata` | `[dbMetadataSchema!]!` | Raw metadata pairs; admin only, empty otherwise. |

## Column fields (new)

| Field | Type | Meaning |
|-------|------|---------|
| `readable` | `Boolean!` | `false` when the caller may not read this column's values. A **masked** column (`read-requires` with `deny-mode: null`) is `readable: false` yet stays **selectable** — the selection succeeds with the value nulled. A refuse-denied column is absent from the list entirely. |
| `writable` | `Boolean!` | `false` when the caller may not write the column (`write-requires` grant unmet, or `policy-write-deny` applies). Column-level only; the update/delete actions themselves are reported by `allowedActions`. |

## Root grant fields

| Field | Type | Meaning |
|-------|------|---------|
| `_grants` | `[String!]!` | The **caller's own** grant set: the union of roles and permissions, sorted. What a client reads to decide what its user may do. |
| `_policyGrants` | `[String!]!` | Every grant name referenced anywhere in the model's policy metadata — action brackets, deny roles, row-scope roles/exemptions, column `read-requires`/`write-requires` — sorted and de-duplicated. The catalogue an app's profile editor lists. It names grants, never which caller holds them. |
