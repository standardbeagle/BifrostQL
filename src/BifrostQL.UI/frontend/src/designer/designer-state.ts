/**
 * Pure state model for the Access-style visual query designer.
 *
 * The React components (palette, canvas, join editor, criteria grid) are thin
 * shells over these immutable operations; all the logic lives here so it can be
 * unit-tested without a DOM. `toSpec` projects the designer state into the
 * VisualQuerySpec the bridge sends to the server-side builder.
 *
 * This slice owns tables + column selection. Joins and the criteria/filter tree
 * are layered on by later slices but already have a home in the state shape.
 */

import type {
  VisualQuerySpec,
  VisualColumn,
  VisualJoin,
  VisualJoinType,
  VisualFilter,
  VisualSort,
} from "../lib/visual-query";
import type { BuilderSchema } from "../lib/builder-bridge";

/** A table placed on the canvas. `table` is the qualified "schema.name". */
export interface PlacedTable {
  table: string;
  /** Set only when the same table is placed more than once (self-join). */
  alias: string | null;
}

/** A column the user has selected to show/sort/filter on. */
export interface PlacedColumn {
  /** Reference to the owning placed table — its alias when set, else its qualified name. */
  tableRef: string;
  column: string;
  show: boolean;
  sort: VisualSort;
  sortOrder: number | null;
  alias: string | null;
}

export interface DesignerState {
  tables: PlacedTable[];
  columns: PlacedColumn[];
  joins: VisualJoin[];
  filter: VisualFilter | null;
  rowLimit: number | null;
}

export const emptyDesignerState: DesignerState = {
  tables: [],
  columns: [],
  joins: [],
  filter: null,
  rowLimit: null,
};

/** The reference token used everywhere (columns, joins) to point at a placed table. */
export function tableRef(t: PlacedTable): string {
  return t.alias ?? t.table;
}

function unqualifiedName(qualified: string): string {
  const dot = qualified.indexOf(".");
  return dot >= 0 ? qualified.slice(dot + 1) : qualified;
}

/**
 * Adds a table to the canvas. The first instance keeps `alias: null` (referenced
 * by its qualified name); any further instance of the same table gets a unique
 * alias so the builder can tell them apart (self-join support).
 */
export function addTable(state: DesignerState, qualified: string): DesignerState {
  const existing = state.tables.filter((t) => t.table === qualified);
  let alias: string | null = null;
  if (existing.length > 0) {
    const base = unqualifiedName(qualified);
    let n = existing.length + 1;
    const taken = new Set(state.tables.map(tableRef));
    while (taken.has(`${base}_${n}`)) n += 1;
    alias = `${base}_${n}`;
  }
  return { ...state, tables: [...state.tables, { table: qualified, alias }] };
}

/** Removes a placed table (by ref) plus its columns and any joins touching it. */
export function removeTable(state: DesignerState, ref: string): DesignerState {
  const target = state.tables.find((t) => tableRef(t) === ref);
  if (!target) return state;

  return {
    ...state,
    tables: state.tables.filter((t) => tableRef(t) !== ref),
    columns: state.columns.filter((c) => c.tableRef !== ref),
    joins: state.joins.filter((j) => j.leftTable !== ref && j.rightTable !== ref),
  };
}

/**
 * Toggles whether a column appears in the SELECT list. Checking a column that
 * isn't tracked yet adds it (shown); unchecking a column that has no sort drops
 * it entirely; unchecking a sorted column keeps the row but clears `show`.
 */
export function toggleColumnShow(
  state: DesignerState,
  ref: string,
  column: string
): DesignerState {
  const idx = state.columns.findIndex((c) => c.tableRef === ref && c.column === column);
  if (idx < 0) {
    const placed: PlacedColumn = {
      tableRef: ref,
      column,
      show: true,
      sort: "none",
      sortOrder: null,
      alias: null,
    };
    return { ...state, columns: [...state.columns, placed] };
  }

  const current = state.columns[idx];
  // Unchecking a column that isn't sorted removes it; otherwise keep it for the
  // sort but mark it hidden.
  if (current.show && current.sort === "none") {
    return { ...state, columns: state.columns.filter((_, i) => i !== idx) };
  }
  const next = { ...current, show: !current.show };
  return { ...state, columns: state.columns.map((c, i) => (i === idx ? next : c)) };
}

/** Whether a column is currently shown on the canvas. */
export function isColumnShown(state: DesignerState, ref: string, column: string): boolean {
  return state.columns.some((c) => c.tableRef === ref && c.column === column && c.show);
}

// ---- joins ---------------------------------------------------------------

function joinsEqual(a: VisualJoin, b: VisualJoin): boolean {
  return (
    a.leftTable === b.leftTable &&
    a.rightTable === b.rightTable &&
    a.leftColumns.join(",") === b.leftColumns.join(",") &&
    a.rightColumns.join(",") === b.rightColumns.join(",")
  );
}

/** Adds a join unless an equivalent one (same tables + columns) already exists. */
export function addJoin(state: DesignerState, join: VisualJoin): DesignerState {
  if (state.joins.some((j) => joinsEqual(j, join))) return state;
  return { ...state, joins: [...state.joins, join] };
}

export function removeJoin(state: DesignerState, index: number): DesignerState {
  return { ...state, joins: state.joins.filter((_, i) => i !== index) };
}

export function setJoinType(state: DesignerState, index: number, type: VisualJoinType): DesignerState {
  return {
    ...state,
    joins: state.joins.map((j, i) => (i === index ? { ...j, type } : j)),
  };
}

/**
 * Finds candidate joins connecting a freshly-placed table to the tables already
 * on the canvas, derived from the schema's FK relationships. Column references
 * are rewritten to the placed tables' refs (alias-aware). Returns:
 * 0 = no relationship (manual join needed), 1 = unambiguous (apply it),
 * 2+ = ambiguous (let the user pick).
 */
export function autoJoinCandidates(
  state: DesignerState,
  schema: BuilderSchema,
  newRef: string
): VisualJoin[] {
  const placedNew = state.tables.find((t) => tableRef(t) === newRef);
  if (!placedNew) return [];

  const others = state.tables.filter((t) => tableRef(t) !== newRef);
  const candidates: VisualJoin[] = [];

  for (const other of others) {
    for (const rel of schema.relationships) {
      const connectsForward = rel.leftTable === placedNew.table && rel.rightTable === other.table;
      const connectsReverse = rel.leftTable === other.table && rel.rightTable === placedNew.table;
      if (!connectsForward && !connectsReverse) continue;

      const leftRef = connectsForward ? tableRef(placedNew) : tableRef(other);
      const rightRef = connectsForward ? tableRef(other) : tableRef(placedNew);
      candidates.push({
        leftTable: leftRef,
        leftColumns: [...rel.leftColumns],
        rightTable: rightRef,
        rightColumns: [...rel.rightColumns],
        type: "inner",
      });
    }
  }

  return candidates;
}

/**
 * Places a table and, when the schema reveals exactly one FK path to an existing
 * table, wires that join automatically. Ambiguous (2+) or absent relationships
 * are left for the join editor. Returns the new state plus the ambiguous
 * candidates (empty unless the caller needs to prompt).
 */
export function addTableWithAutoJoin(
  state: DesignerState,
  schema: BuilderSchema,
  qualified: string
): { state: DesignerState; ambiguous: VisualJoin[] } {
  const placed = addTable(state, qualified);
  const newRef = tableRef(placed.tables[placed.tables.length - 1]);
  const candidates = autoJoinCandidates(placed, schema, newRef);

  if (candidates.length === 1) {
    return { state: addJoin(placed, candidates[0]), ambiguous: [] };
  }
  return { state: placed, ambiguous: candidates.length > 1 ? candidates : [] };
}

/** Projects the designer state into the VisualQuerySpec sent over the bridge. */
export function toSpec(state: DesignerState): VisualQuerySpec {
  const columns: VisualColumn[] = state.columns.map((c) => ({
    table: c.tableRef,
    column: c.column,
    alias: c.alias,
    show: c.show,
    sort: c.sort,
    sortOrder: c.sortOrder,
  }));

  return {
    tables: state.tables.map((t) => ({ table: t.table, alias: t.alias })),
    columns,
    joins: state.joins,
    filter: state.filter,
    rowLimit: state.rowLimit,
  };
}
