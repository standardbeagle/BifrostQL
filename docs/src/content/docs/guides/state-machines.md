---
title: State Machines
description: Enforce lifecycle transitions with metadata-defined state machines.
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

Use `|` between transitions in metadata rule strings. The rule parser already uses semicolons between properties, so pipe-separated transitions avoid ambiguity in `appsettings.json` and sample metadata files.

## Enforcement

For inserts, BifrostQL allows the configured initial state. For updates, it reads the current row, compares the existing state to the requested state, and accepts only a configured transition. Updating other fields without changing the state is allowed.

Role qualifiers in `[]` are checked against the authenticated user context. The default `admin` role bypasses policy and state-machine role checks, matching the rest of the authorization pipeline.

Rejected mutations return a generic error:

```text
State transition is not permitted.
```

The message intentionally omits table, state, and role details so raw GraphQL callers cannot use transition failures to discover the lifecycle configuration.

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
