---
title: "ER Diagram"
description: "A read-only entity-relationship diagram of the connected database, laid out automatically over the introspected relationship data — foreign keys, many-to-many pairs, name-based links, and polymorphic relationships each rendered as a distinct edge kind."
---

The workbench includes a **read-only ER diagram** of the connected database,
built with React Flow and an ELK layered layout over the same introspected
relationship data the editor already consumes — it adds no schema-mutating call
and no new server endpoint.

## What it renders

- **Nodes** are tables (name, primary-key badge, column list collapsed by
  default and expandable on click).
- **Edges** distinguish relationship kinds: one directed edge per foreign key;
  exactly one edge per many-to-many pair with the junction table collapsed
  (not drawn as its own node); name-based links as a visually distinct edge
  kind; and polymorphic relationships as an **annotated** edge (never silently
  dropped).
- Hovering an edge reveals its join columns.

## Large schemas

For schemas with many tables, **schema-name clustering** and an **N-hop
neighborhood filter** reduce the rendered node count so a large schema stays
legible; the layout of a 100-table schema completes well within interactive
time.

## Interactions

- **Clicking a table node** switches the shell to the editor view with that
  table's grid loaded.
- A minimap and zoom aid navigation; the diagram can be exported to PNG
  client-side.

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/)
- [Schema generation](/BifrostQL/concepts/schema-generation/) — where the
  relationship data comes from.
