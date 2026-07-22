---
title: "Saved Queries"
description: "Persist a visual-query-builder design as a saved-object query, then list, reopen, and run it — with non-destructive schema-drift handling when a referenced table or column disappears."
---

The [visual query builder](/BifrostQL/concepts/visual-query-builder/) designs a
multi-table `SELECT` without writing SQL. **Saved queries** let you persist that
design, reopen it later, and run it — backed by the unified
[saved-object store](/BifrostQL/concepts/saved-objects/) as `type: query`.

## Save, reopen, run

- **Save / Save-as / rename / delete** from the designer, with a dirty-state
  indicator. Renaming keeps the object **id stable**; delete asks for
  confirmation.
- The saved-query **list** appears in the shell navigation; opening one restores
  the designer state and runs it through the existing designer execute path into
  the results grid.
- The design round-trips: design → save → restart the app → reopen → run.

The serialized definition is the visual-query structure (tables, joins, columns,
filter tree, sort, limit). No user-supplied table or field name is inserted into
GraphQL or SQL text — the builder reuses the same schema-derived validation
helpers as the live designer.

## Schema drift is non-destructive

When you open a saved query, its restored state is checked against live schema
introspection. If a referenced table or column has been dropped from the
database, the query opens in a **degraded mode**: a non-destructive warning
lists the broken references, and the stored definition is **never
auto-rewritten**. Drift is derived from the restored designer state itself
(including columns referenced only from a nested filter), so a stale or empty
fingerprint cannot hide a real break.

`Save` and `Save-as` are blocked (or gated behind a confirmation naming the
still-broken references) while a design is in degraded mode, so a drifted
definition cannot be silently written back to the store.

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/)
- [Saved objects](/BifrostQL/concepts/saved-objects/) — the store and its
  optimistic-concurrency model.
