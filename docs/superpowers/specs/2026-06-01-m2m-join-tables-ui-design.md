# Many-to-Many Join Tables in the UI — Design

Date: 2026-06-01
Status: Approved (Approach A)

## Problem

When a parent→child relationship is actually many-to-many (through a junction
table), the UI treats the junction as just another child collection. Drilling
parent → child → sub-child lands on the *junction* rows instead of the related
target entity, breaking the natural navigation chain. The server already
detects `ManyToManyLinks` in `DbModel` and already exposes a queryable target
field on the parent GraphQL type, but neither the edit-db `_dbSchema` nor the
designer `get-builder-schema` surfaces the m2m information, so the UI cannot
tell a junction from an ordinary child.

## Goal

- Surface m2m relationships to both UI surfaces (edit-db SPA + desktop designer)
  via the server schema.
- In edit-db, present an m2m relationship as a single tab that shows the
  **target** rows (skipping the junction for navigation), with an indicator and
  an inline expand to reveal the junction payload.
- Drill parent → target → sub-child correctly.
- Support attach/detach of links (insert/delete junction rows) in this
  iteration.
- Handle junction tables that carry payload columns (skip-with-payload).
- Keep the design extensible to the other presentation modes (FK selector,
  multi-select) and to app-metadata overrides later.

## Approach A (chosen)

The m2m tab queries the **junction rows** for the parent (the existing
multiJoin) and pulls the target's display columns through the junction's own
single-join to the target, in one query:

```
Parent(filter:{pk}) {
  Enrollments {              # existing multiJoin to junction
    grade, enrolled_on       # junction payload
    Courses { id, title }    # junction singleJoin → target display
  }
}
```

- Collapsed (default): target columns only — reads like the parent's Courses.
- Expand ("via Enrollments"): reveal junction payload + FK columns inline.
- Drill: click a target row → push a drill frame on the target table.
- Attach: insert a junction row `{parentFk, targetFk, …payload}`.
- Detach: delete the junction row by its PK (already present in the row).

One round-trip; payload and the junction PK for detach fall out naturally.
Rejected: B (target-rows via parent m2m field — needs a 2nd query + client
join for payload/detach), C (app-metadata-only — does not fix the default
broken drill without per-relationship config; kept as a future override path).

## Section 1 — Server contract

### 1a. Detection — `ManyToManyDetectionStrategy` + `ManyToManyLink`

- Add `bool HasPayload { get; init; }` to `ManyToManyLink`.
- `AutoDetect`: remove the `nonKeyColumns.Count > 0 → continue` early-out;
  instead compute `hasPayload = nonKeyColumns.Count > 0` and set it on the link.
  The two-FK requirement and `IsComposite` exclusion stay unchanged.

### 1b. edit-db projection — `MetaSchemaResolver`

New field on each table, parallel to `multiJoins`/`singleJoins`:

```csharp
manyToManyJoins = t.ManyToManyLinks.Values.Select(m => new {
    name                     = m.JunctionTable.GraphQlName,
    targetTable              = m.TargetTable.GraphQlName,
    junctionTable            = m.JunctionTable.GraphQlName,
    junctionTargetField      = /* junction.SingleLinks[target].ParentFieldName */,
    sourceColumnNames        = new[]{ m.SourceColumn.GraphQlName },
    junctionSourceColumnNames= new[]{ m.JunctionSourceColumn.GraphQlName },
    junctionTargetColumnNames= new[]{ m.JunctionTargetColumn.GraphQlName },
    targetColumnNames        = new[]{ m.TargetColumn.GraphQlName },
    hasPayload               = m.HasPayload,
})
```

UI uses the existing multiJoin to the junction for the rows query;
`manyToManyJoins` tells it which multiJoin is a junction, the target table to
drill to, and the junction→target field to nest. The UI **suppresses** any
multiJoin whose destination is a `junctionTable` here.

Consequences:
- Relaxing `AutoDetect` makes payload junctions emit a new m2m target field on
  the parent GraphQL type (`TableSchemaGenerator:82`). Additive.
- A junction appears in both `multiJoins` and `manyToManyJoins`; UI dedupe
  handles it. Junction stays independently browsable from its own table view.

### 1c. designer projection — `BuilderSchema.cs`

Add an m2m link list to `BuilderSchemaDto`:
`{ sourceTable, targetTable, junctionTable, sourceColumns[],
   junctionSourceColumns[], junctionTargetColumns[], targetColumns[] }`,
projected from `model.Tables.SelectMany(t => t.ManyToManyLinks.Values)`.

## Section 2 — edit-db UI

### 2a. Schema type (`types/schema.ts`)

```ts
export interface ManyToManyJoin {
  name: string;
  targetTable: string;
  junctionTable: string;
  junctionTargetField: string;
  sourceColumnNames: string[];
  junctionSourceColumnNames: string[];
  junctionTargetColumnNames: string[];
  targetColumnNames: string[];
  hasPayload: boolean;
}
// Table gains: manyToManyJoins: ManyToManyJoin[];
```

### 2b. New pure lib `lib/m2m.ts` (dependency-free, unit-tested)

- `junctionTableNames(table): Set<string>` — junctions to suppress.
- `detailTabs(table): DetailTab[]` — plain multiJoins (minus junctions) + one
  m2m tab per `manyToManyJoins`, tagged `kind: 'child' | 'm2m'`.
- `m2mRowsQuery(parent, m2m, parentRowId)` — one-shot junction-rows-with-target
  query.
- `attachJunctionDetail(m2m, parentRow, targetRow)` — insert detail for the
  junction row.
- detach reuses `pkFilterFor` on the junction row.

### 2c. New component `components/m2m-panel.tsx`

- Grid of junction rows with target columns flattened from
  `row[junctionTargetField]`.
- Collapsed: target display columns only. Expand toggle "via {junctionLabel}"
  reveals payload + junction FK columns inline. No toggle when nothing extra to
  reveal (badge only).
- Row click → drill into target (frame on `targetTable`, filtered by target PK).
- Row action Detach → `useDeleteMutation` on junction by row PK + confirm-dialog.
- Toolbar "+ Add link" → target-row picker (reuse `fk-cell-popover` selection)
  → `useTableMutation(junction).insert`. If `hasPayload`, open the new row
  inline for payload edit (`data-edit`).

### 2d. `detail-panel.tsx`

Replace `parentTable.multiJoins` tab source with `detailTabs(parentTable)`.
Each tab renders the existing `DataDataTable` (kind `child`) or `<M2mPanel>`
(kind `m2m`). Junction tabs no longer appear raw.

### 2e. Drill (`drill-stack.ts` / `data-panel.tsx`)

M2m drill = frame on `targetTable` filtered by target PK — fits the existing
`ColumnPanel`/`DrillFrame` shape, no struct change. Verify `buildDrillCrumbs`
resolves the target label (it does, via `lookup`).

Reuse: `useTableMutation`, `useDeleteMutation`, `pkFilterFor`,
`fk-cell-popover`, `confirm-dialog`, `data-edit`, `DataDataTable`.
New: `lib/m2m.ts`, `m2m-panel.tsx`.

## Section 3 — Desktop designer

- `builder-bridge.ts`: add `BuilderManyToMany` interface + `manyToMany` field on
  `BuilderSchema`.
- `designer-state.ts`: when placing a table that has an m2m path to an existing
  placed table, offer a "join through {junction}" candidate that auto-adds the
  junction table to the canvas plus both FK joins. New helper
  `m2mJoinThroughJunction`; ambiguity handling parallels existing
  `autoJoinCandidates`.
- `JoinEditor.tsx`: indicate a through-junction join.

## Section 4 — Testing

- Server (xUnit + FluentAssertions):
  - `ManyToManyDetectionStrategy`: payload junction now detected; `HasPayload`
    set; pure junction still `HasPayload == false`; composite-FK still excluded.
  - `MetaSchemaResolver`: `manyToManyJoins` projected with correct fields.
  - `BuilderSchema`: m2m links projected.
- edit-db (vitest):
  - `lib/m2m.test.ts`: `detailTabs` suppression + tagging, `m2mRowsQuery` shape,
    `attachJunctionDetail`, detach filter.
  - `m2m-panel` behavior tests following `data-table.test` patterns.
- designer (vitest):
  - `designer-joins.test.ts`: through-junction candidate adds junction + both
    joins; ambiguity path.

## Out of scope (this iteration)

- Composite-FK junctions.
- FK-selector and multi-select presentation modes.
- App-metadata override of m2m presentation (future layer on top).
