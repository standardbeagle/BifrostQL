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
  VisualFilterOperator,
  VisualSort,
} from "../lib/visual-query";
import type { BuilderSchema } from "../lib/builder-bridge";

/** A single criterion cell in the QBE grid (one operator + value). */
export interface CriterionExpr {
  operator: VisualFilterOperator;
  /** Scalar for most ops; array for _in/_between; ignored for _null. */
  value: unknown;
}

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
  /**
   * Criteria by OR-row index, mirroring the Access grid: index 0 is the
   * "Criteria" row, 1+ are the "Or" rows. A null slot means no criterion for
   * this field in that row. Criteria in the same OR-row across fields are ANDed;
   * different OR-rows are ORed.
   */
  criteria: (CriterionExpr | null)[];
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
    return { ...state, columns: [...state.columns, newColumn(ref, column, true)] };
  }

  const current = state.columns[idx];
  // Unchecking a column that isn't sorted and has no criteria removes it;
  // otherwise keep it for the sort/criteria but mark it hidden.
  if (current.show && current.sort === "none" && !hasAnyCriterion(current)) {
    return { ...state, columns: state.columns.filter((_, i) => i !== idx) };
  }
  const next = { ...current, show: !current.show };
  return { ...state, columns: state.columns.map((c, i) => (i === idx ? next : c)) };
}

/** Whether a column is currently shown on the canvas. */
export function isColumnShown(state: DesignerState, ref: string, column: string): boolean {
  return state.columns.some((c) => c.tableRef === ref && c.column === column && c.show);
}

// ---- criteria grid (sort + filter) ---------------------------------------

function newColumn(ref: string, column: string, show: boolean): PlacedColumn {
  return { tableRef: ref, column, show, sort: "none", sortOrder: null, alias: null, criteria: [] };
}

function hasAnyCriterion(c: PlacedColumn): boolean {
  return c.criteria.some((x) => x != null);
}

function findColumn(state: DesignerState, ref: string, column: string): number {
  return state.columns.findIndex((c) => c.tableRef === ref && c.column === column);
}

/** Ensures a column row exists in the grid (hidden by default) so it can carry sort/criteria. */
export function ensureColumn(state: DesignerState, ref: string, column: string): DesignerState {
  if (findColumn(state, ref, column) >= 0) return state;
  return { ...state, columns: [...state.columns, newColumn(ref, column, false)] };
}

/**
 * Sets a column's sort direction. Becoming sorted assigns the next sortOrder
 * (so multi-column sort follows the order columns were sorted); clearing the
 * sort resets the order.
 */
export function setColumnSort(
  state: DesignerState,
  ref: string,
  column: string,
  sort: VisualSort
): DesignerState {
  const ensured = ensureColumn(state, ref, column);
  const maxOrder = ensured.columns.reduce((m, c) => Math.max(m, c.sortOrder ?? 0), 0);
  return {
    ...ensured,
    columns: ensured.columns.map((c) => {
      if (c.tableRef !== ref || c.column !== column) return c;
      if (sort === "none") return { ...c, sort, sortOrder: null };
      return { ...c, sort, sortOrder: c.sortOrder ?? maxOrder + 1 };
    }),
  };
}

/**
 * Sets (or clears, with null) the criterion for a column at the given OR-row
 * index. Pads the criteria array so the index is addressable.
 */
export function setColumnCriterion(
  state: DesignerState,
  ref: string,
  column: string,
  orIndex: number,
  criterion: CriterionExpr | null
): DesignerState {
  const ensured = ensureColumn(state, ref, column);
  return {
    ...ensured,
    columns: ensured.columns.map((c) => {
      if (c.tableRef !== ref || c.column !== column) return c;
      const criteria = c.criteria.slice();
      while (criteria.length <= orIndex) criteria.push(null);
      criteria[orIndex] = criterion;
      return { ...c, criteria };
    }),
  };
}

/**
 * Builds the VisualFilter tree from the grid: for each OR-row index, AND the
 * criteria present across all columns; OR those per-row groups together. Returns
 * null when there are no criteria, the bare group when there's only one.
 */
export function toFilter(state: DesignerState): VisualFilter | null {
  const maxRows = state.columns.reduce((m, c) => Math.max(m, c.criteria.length), 0);

  const groups: VisualFilter[] = [];
  for (let orIndex = 0; orIndex < maxRows; orIndex++) {
    const leaves: VisualFilter[] = [];
    for (const col of state.columns) {
      const expr = col.criteria[orIndex];
      if (!expr) continue;
      leaves.push({
        op: "leaf",
        children: null,
        criterion: {
          table: col.tableRef,
          column: col.column,
          operator: expr.operator,
          value: expr.value,
        },
      });
    }
    if (leaves.length === 0) continue;
    groups.push(leaves.length === 1 ? leaves[0] : { op: "and", children: leaves, criterion: null });
  }

  if (groups.length === 0) return null;
  if (groups.length === 1) return groups[0];
  return { op: "or", children: groups, criterion: null };
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

function coerceScalar(text: string): unknown {
  const t = text.trim();
  if (t === "") return "";
  // Numeric literal? keep as number; otherwise a string.
  const n = Number(t);
  return Number.isFinite(n) && t === String(n) ? n : t;
}

/**
 * Turns a grid cell's operator + raw text into a {@link CriterionExpr} value:
 * arrays for `_in`/`_between` (comma-separated), `true` for `_null` (IS NULL),
 * a coerced scalar otherwise.
 */
export function parseCriterionValue(operator: VisualFilterOperator, text: string): unknown {
  switch (operator) {
    case "_null":
      return true;
    case "_in":
    case "_between":
      return text
        .split(",")
        .map((s) => s.trim())
        .filter((s) => s.length > 0)
        .map(coerceScalar);
    default:
      return coerceScalar(text);
  }
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
    // Filter is derived from the criteria grid; state.filter is retained only as
    // an escape hatch for a directly-supplied tree.
    filter: state.filter ?? toFilter(state),
    rowLimit: state.rowLimit,
  };
}
