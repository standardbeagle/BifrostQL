---
title: "Row and Column Authorization Policies"
description: "Restrict which tables, rows, and columns a role may read or write using table metadata alone, enforced by one evaluator every query, mutation, and adapter uses."
---

Authorization policies decide what an authenticated caller may do. You declare them in
table metadata, and one evaluator enforces them on every read, every write, and every
schema surface — GraphQL, MCP, pgwire, gRPC, OData, and S3 alike.

Authentication and authorization are separate jobs here.
[Authentication](/BifrostQL/guides/authentication/) establishes *who* the caller is and
projects it into a user context. This guide covers what that identity is then allowed to
touch. Authorization reads the identity; it never establishes one.

The default is `allow`: a table with no `policy-*` metadata is unrestricted. For production,
prefer `:root { policy-default: deny }`; undeclared tables then deny all actions and disappear
from schema descriptions. An administrator still bypasses policy; set `policy-admin-role` to a
role nobody holds when no escape is wanted. Selector rules count as declarations, so a rule such
as `public.*|has(account_id) { policy-actions: read,create,update,delete }` can establish the
common default and individual tables can narrow it.

## Grants

**Grants** are the case-insensitive union of the caller's roles and permissions.
Every `-roles` policy key accepts any grant, so a permission can satisfy these
keys exactly like a role.

## Declare a policy

```text
main.orders { policy-actions: read,update }
main.orders { policy-read-deny: secret }
main.orders { policy-write-deny: secret }
main.documents { policy-read-deny: body }
```

| Key | Meaning |
|---|---|
| `policy-actions` | Comma list of `read`, `create`, `update`, `delete`. Anything absent is denied. |
| `policy-read-deny` | Columns this table never returns. |
| `policy-read-deny-roles` | Grants the read-deny applies to. Omit it to deny every non-admin. |
| `policy-write-deny` | Columns no mutation may write. |
| `policy-write-deny-roles` | Grants the write-deny applies to. Omit it to deny every non-admin. |
| `policy-row-scope` | A predicate binding a column to a context value. |
| `policy-row-scope-roles` | Grants the row scope applies to. Omit it to scope every non-admin. |

Two consequences follow from the shape of `policy-actions`:

- It is an allow-list. `main.documents` above declares column denials with no
  `policy-actions`, so **every read of it is denied** — the grant is missing.
- An unknown token fails model load with the valid action names in the message. Dropping a
  typo silently would leave an empty allow-list, which reads as "no policy" and grants
  everything.

The `admin` role bypasses every check. Name a different role when you register the
evaluator if `admin` means something else in your model.

## Scope rows to the caller

`policy-row-scope` takes exactly one term: a column, `=`, and a context key in braces.

```text
main.members { policy-actions: read,create,update,delete }
main.members { policy-row-scope: user_id = {user_id} }
main.members { policy-row-scope-roles: member }
main.households { policy-row-scope: household_id = {household_id} }
```

The compiler turns that into a filter the pipeline ANDs alongside the tenant filter, so
the predicate is parameterized and composes with every other transformer. Equality is the
only operator. There are no functions, no `AND`, and no `OR` — a policy language rich
enough to be interesting is rich enough to be wrong in a way nobody notices.

Both failure modes are fail-closed and answer with a generic message:

- A malformed expression is a misconfiguration and refuses the query.
- A missing context value refuses the query. A caller with no `user_id` sees no rows
  rather than every row.

Row scope applies to reads, updates, and deletes. An insert has no existing row to scope,
so the scope is skipped there — constrain inserts with `policy-actions` instead.

## Deny columns

Column gating has two directions and two flavours. The deny lists refuse the
request rather than silently stripping the field, so a client learns its query
was wrong instead of quietly receiving less than it asked for. Both deny lists
are qualified by a `-roles` key that narrows the denial to the grants listed;
omitting it denies every non-admin caller.

| Direction | Deny | Qualifier | Grant gate |
|---|---|---|---|
| Read | `policy-read-deny` | `policy-read-deny-roles` | — |
| Write | `policy-write-deny` | `policy-write-deny-roles` | `write-requires` (column selector) |

`write-requires` is the write side's grant dimension: a column-selector rule
naming the grants, any one of which the caller must hold to write the column.
The gate is **presence-keyed** — sending the column at all is a write, even
sending the value already stored; an update that does not touch the column is
unaffected. It never applies to a delete (a delete carries no writable columns;
`policy-actions` is the delete tool). A two-row example:

```text
main.dues_invoices { policy-read-deny: amount_cents }
main.dues_invoices { policy-read-deny-roles: officer,event_manager,member,read_only }
main.users.cost_rate { write-requires: team.manage }
```

Here `amount_cents` is hidden from the listed roles but readable by finance,
while `cost_rate` is writable only by a `team.manage` holder and readable by
everyone. When both gates name the same column, precedence is requires first,
then deny: holding the grant lets the caller past `write-requires`, but a
matching `policy-write-deny` entry still refuses the write.

Two schema consequences follow from the write side:

- A column write-denied for **every** caller (unconditional `policy-write-deny`,
  no roles) leaves the table's insert/update input types entirely, so a NOT NULL
  server-maintained column does not make the table un-insertable.
- A grant-conditional column (a role-qualified deny, or `write-requires`) stays
  in the shared input type but becomes optional there, so a caller without the
  grant can omit it. The mutation pipeline remains the enforcement backstop.

## What the caller sees

Denials are deliberately uninformative. A denied read or write answers
`Access denied by authorization policy.` with the `ACCESS_DENIED` code and no table,
column, or action name. A referenced-but-denied field answers
`The query references a field that is not permitted by authorization policy.` Both codes
map to the same wire status on every protocol adapter, so a caller cannot tell a denial
from a miss by watching status codes.

## The schema surface hides denied tables

Every introspection surface projects the model through the same evaluator before
describing anything. A table the caller may not read is absent from the description; a
column the caller may not read is absent from its table; and a foreign-key edge is
published only when both end tables and all key columns are visible.

Three properties are worth knowing:

- **A policy that fails to evaluate hides the table, including from admins.** The parse
  throws before the admin bypass runs, so a broken policy costs visibility rather than
  leaking data.
- **Denied and nonexistent look identical.** A lookup returns nothing in both cases, so
  the schema surface is no existence oracle.
- **Visibility governs description, not enforcement.** Hiding a table from a catalog does
  not authorize the data path. Adapters still route the request through the transformer
  pipeline for the authoritative answer.

This is why the adapters share one evaluator instead of each checking metadata their own
way — see the fourth invariant in `.claude/rules/protocol-adapter-security.md`. The MCP
schema tools, the pgwire catalog, gRPC reflection, the OData `$metadata` document, and the
app-metadata overlay all call it.

## Gate a state transition by role

A state machine's transitions can require a role, checked through the same evaluator:

```text
main.orders { state-column: status }
main.orders { initial-state: draft }
main.orders { states: draft,submitted,approved }
main.orders { transitions: draft->submitted; submitted->approved[manager] }
```

A transition with no role list is open to any caller who passes the table's policy. The
bracketed roles on `submitted->approved` restrict that edge to `manager` and to admins.
See [Metadata-Defined State Machines](/BifrostQL/guides/state-machines/) for the full
transition syntax.

## Where enforcement happens

Two transformers carry the policy, both at priority 1 — immediately after tenant isolation
at priority 0, and ahead of every application-level module:

- The **read** transformer refuses a denied table, injects the row-scope filter, and
  asserts every requested column is readable.
- The **write** transformer maps the mutation to its action (`insert` to create, and so
  on), refuses a denied action or a write-denied column, and attaches the row-scope filter
  to updates and deletes.

Neither can be skipped. A protocol adapter reaches data through `IQueryIntentExecutor` or
`IMutationIntentExecutor`, and both run the full transformer chain, so no front door has an
API that bypasses policy.

## Related

- [GraphQL API Authentication and OIDC](/BifrostQL/guides/authentication/) — establishing
  the identity policies are evaluated against.
- [Multi-Tenant Organization Data Model](/BifrostQL/guides/org-model/) — tenant isolation,
  the priority-0 filter policy runs behind.
- [One Pipeline, Many Front Doors](/BifrostQL/concepts/protocol-adapters/) — why every
  adapter shares this evaluator.
- [Workflow Mutations and Audit Trail](/BifrostQL/guides/workflow-mutations/) — sidecar
  operations gated by the same engine.
