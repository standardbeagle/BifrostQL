---
title: "Metadata-Defined State Machines"
description: "Declare the allowed status transitions in table metadata and let the mutation pipeline reject every write that would move a row into an illegal next state."
---

BifrostQL can enforce row lifecycle rules from table metadata. A state machine names the state column, the initial insert state, the allowed states, and the transitions that may move a row from one state to another.

State machines run inside the mutation transformer pipeline, so direct GraphQL requests, workflow mutations, and generated UI calls all use the same checks.

## Metadata

Add the state-machine keys to a table metadata rule:

```text
main.members {
  state-column: status;
  initial-state: pending;
  states: pending,active,inactive,deceased;
  transitions: pending->active[officer,admin]@member.activated|active->inactive[officer,admin]@member.inactivated|inactive->active[admin]@member.reactivated|active->deceased[officer,admin]@member.deceased|inactive->deceased[officer,admin]@member.deceased
}
```

The keys are:

- `state-column`: the column that stores the current lifecycle state.
- `initial-state`: the only state accepted when an insert supplies the state column.
- `states`: comma-separated allowed state names.
- `transitions`: allowed transitions in `from->to[roles]@event` form.
- `transition-not-by` (optional): comma-separated `from->to:column` rules. A caller whose audit
  actor equals the row's `column` may not take that transition — "the approver is not the author".
  The column must declare `populate: created-by`, or model load fails: a column the client writes
  would let the author name someone else, and `updated-by` names the last editor, not the author.
  The comparison ignores case, and two uuids compare as values.

Use `|` between transitions in metadata rule strings. The rule parser already uses semicolons between properties, so pipe-separated transitions avoid ambiguity in `appsettings.json` and sample metadata files.

## Enforcement

For inserts, BifrostQL allows the configured initial state. For updates, it reads the current row, compares the existing state to the requested state, and accepts only a configured transition. Updating other fields without changing the state is allowed.

The current-state read runs inside the update's transaction and takes the dialect's update lock (`WITH (UPDLOCK)` on SQL Server, `FOR UPDATE` on PostgreSQL and MySQL; SQLite serializes writers), so a concurrent transition of the same row waits instead of validating against the same stale state. The `UPDATE` also re-checks the state it was validated from: `state = <loaded state>` is ANDed into its `WHERE`. If the row left that state (or disappeared) between the read and the write, the update affects no rows and fails with a `CONFLICT` error and rolls back, rather than applying a transition nobody validated. This holds for state-preserving updates too — writing `open` over a row that just moved to `review` would be an unvalidated `review -> open`. Reload the row and retry on `CONFLICT`.

Role qualifiers in `[]` are checked against the authenticated user context. The role the model names in `:root { policy-admin-role }` bypasses policy and state-machine role checks, matching the rest of the [authorization pipeline](/BifrostQL/guides/authorization/#the-admin-role); with no such key, no role does.

Rejected mutations return a generic error:

```text
State transition is not permitted.
```

The message intentionally omits table, state, and role details so raw GraphQL callers cannot use transition failures to discover the lifecycle configuration.

### The approver is not the author

```text
main.claims {
  state-column: status;
  initial-state: draft;
  states: draft,submitted,approved;
  transitions: draft->submitted|submitted->approved[manager];
  transition-not-by: submitted->approved:created_by
}
main.claims.created_by { populate: created-by }
```

On `submitted -> approved`, BifrostQL reads the row's `created_by` inside the update's transaction
and compares it with the caller's audit actor, the claim named by `:root { user-audit-key }` (the
same value `populate: created-by` stamps). If they are equal the transition is refused with the
generic message above. The rule holds for the `admin` role too, since separation of duties is the
point of it. It fails closed: a caller with no audit actor, or a row whose author column is empty,
is refused, because separation cannot be shown. The named transition must be declared in
`transitions` and the column must exist, or the model fails to load.

`_availableTransitions` applies role checks only. It still lists a transition that
`transition-not-by` will refuse to the row's author.

## Reading available transitions

A client shouldn't have to hard-code which buttons to show — "Activate", "Cancel", "Reactivate" — or guess and get rejected. Every table with a state machine exposes an `_availableTransitions` field that returns the target states the **current caller** may move *this row* to, already filtered by the caller's roles:

```graphql
{
  members {
    data {
      memberId
      status
      _availableTransitions   # e.g. ["active", "inactive"] for an officer
    }
  }
}
```

`_availableTransitions` returns `[String!]` — the reachable next states from the row's current state, with role checks applied. An admin sees the full set; a member with no qualifying role sees `[]`. Drive your UI straight off this list so the actions a user sees always match what the server will accept.

## Audit Trail

Accepted state transitions emit a `StateTransitionInfo` event after the database update succeeds. The default server observer writes an `audit_log` row through `IBifrostWorkflowExecutor`, so the audit insert still traverses BifrostQL's normal GraphQL mutation pipeline.

The default audit action format is:

```text
<entity>.<from>-><to>
```

For example, changing `members.status` from `pending` to `active` writes `members.pending->active`.

## Membership Manager Example

The Hosted SPA Membership Manager sample defines two lifecycle models:

```text
main.members {
  state-column: status;
  initial-state: pending;
  states: pending,active,inactive,deceased;
  transitions: pending->active[officer,admin]@member.activated|active->inactive[officer,admin]@member.inactivated|inactive->active[admin]@member.reactivated|active->deceased[officer,admin]@member.deceased|inactive->deceased[officer,admin]@member.deceased
}

main.events {
  state-column: status;
  initial-state: draft;
  states: draft,published,cancelled;
  transitions: draft->published[event_manager,admin]@event.published|published->cancelled[event_manager,admin]@event.cancelled|draft->cancelled[event_manager,admin]@event.cancelled
}
```

This keeps invalid shortcuts, such as `members.pending -> deceased`, out of the generated CRUD API while still allowing administrative repair paths like `members.inactive -> active`.
