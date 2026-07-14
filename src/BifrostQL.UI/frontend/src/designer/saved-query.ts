/**
 * Serialization + schema-drift model for saved QBE queries.
 *
 * A saved query is stored as a `type: 'query'` saved object (the store from the
 * saved-object slice — the only storage path) whose `definition` is the JSON
 * emitted here: the designer state exactly as designed.
 *
 * Drift is derived from that state, never from a stored side-copy of it. The
 * schema surface a query depends on is a pure function of its state
 * ({@link queryRefs}), so persisting a fingerprint alongside would add a second
 * source of truth that can only ever disagree by being wrong — an empty, stale,
 * or truncated fingerprint would silently under-report drift and let a broken
 * design run. Older definitions may still carry a `fingerprint` field; it is
 * ignored on read and not written back.
 *
 * Detection is read-only by contract: a query whose column was dropped opens in
 * degraded mode with a warning, and the stored definition is never auto-rewritten
 * (the user decides what the query should become).
 *
 * Pure module: no I/O, no React, no GraphQL text. The designer state it carries
 * only ever reaches the server through the existing VisualQuerySpec bridge, so
 * user-typed names stay validated identifiers, never spliced into query text.
 */

import type {
  VisualFilter,
  VisualFilterOperator as VisualCriterionOperator,
  VisualJoin,
} from "../lib/visual-query";
import type { BuilderSchema } from "../lib/builder-bridge";
import {
  tableRef,
  type CriterionExpr,
  type DesignerState,
  type PlacedColumn,
  type PlacedTable,
} from "./designer-state";

export const SAVED_QUERY_KIND = "bifrost.visual-query";
export const SAVED_QUERY_VERSION = 1;

/** A column reference, resolved to the real (never aliased) qualified table. */
export interface ColumnRef {
  table: string;
  column: string;
}

/** The schema surface a saved query depends on — the drift-detection input. */
export interface SchemaFingerprint {
  /** Qualified "schema.name" of every placed table, deduped + sorted. */
  tables: string[];
  /** Every referenced column (selected, sorted, filtered, or joined on), sorted. */
  columns: ColumnRef[];
}

/** The JSON persisted as a saved object's `definition` for `type: 'query'`. */
export interface SavedQueryDefinition {
  kind: typeof SAVED_QUERY_KIND;
  version: typeof SAVED_QUERY_VERSION;
  state: DesignerState;
}

/** Broken references found by {@link detectSchemaDrift}. */
export interface SchemaDrift {
  missingTables: string[];
  /** Columns gone from a table that still exists (a dropped table is reported once, above). */
  missingColumns: ColumnRef[];
}

export function hasDrift(drift: SchemaDrift): boolean {
  return drift.missingTables.length > 0 || drift.missingColumns.length > 0;
}

/** The broken references, one readable line each, for the drift warning. */
export function describeDrift(drift: SchemaDrift): string[] {
  return [
    ...drift.missingTables.map((t) => `table ${t}`),
    ...drift.missingColumns.map((c) => `column ${c.table}.${c.column}`),
  ];
}

/**
 * Set-key for a (table, column) pair. Tab cannot appear in an identifier the
 * designer can reference, so the key stays unambiguous for names containing
 * spaces or dots.
 */
function refKey(table: string, column: string): string {
  return `${table}\t${column}`;
}

function splitRefKey(key: string): ColumnRef {
  const [table, column] = key.split("\t");
  return { table, column };
}

function collectFilterRefs(
  filter: VisualFilter | null | undefined,
  into: (ref: string, column: string) => void
): void {
  if (!filter) return;
  if (filter.criterion) into(filter.criterion.table, filter.criterion.column);
  for (const child of filter.children ?? []) collectFilterRefs(child, into);
}

/**
 * The schema surface a design depends on, derived from the state alone: placed
 * tables, grid columns (shown, sorted, or filtered), join columns, and any
 * directly-supplied filter tree. Aliased refs (self-joins) resolve back to the
 * real table name — an alias is a designer-local token, not a schema object.
 *
 * Deterministic, so drift can always be recomputed from what is on the canvas;
 * nothing derived here is ever persisted.
 */
export function queryRefs(state: DesignerState): SchemaFingerprint {
  const byRef = new Map<string, PlacedTable>(state.tables.map((t) => [tableRef(t), t]));
  const columns = new Set<string>();

  const addRef = (ref: string, column: string) => {
    const placed = byRef.get(ref);
    if (placed) columns.add(refKey(placed.table, column));
  };

  for (const c of state.columns) addRef(c.tableRef, c.column);
  for (const j of state.joins) {
    for (const c of j.leftColumns) addRef(j.leftTable, c);
    for (const c of j.rightColumns) addRef(j.rightTable, c);
  }
  collectFilterRefs(state.filter, addRef);

  return {
    tables: [...new Set(state.tables.map((t) => t.table))].sort(),
    columns: [...columns].sort().map(splitRefKey),
  };
}

/** Projects the designer state into the persisted definition. */
export function serializeQuery(state: DesignerState): SavedQueryDefinition {
  return { kind: SAVED_QUERY_KIND, version: SAVED_QUERY_VERSION, state };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((v) => typeof v === "string");
}

function parseTable(value: unknown): PlacedTable | null {
  if (!isRecord(value)) return null;
  if (typeof value.table !== "string") return null;
  if (value.alias != null && typeof value.alias !== "string") return null;
  return { table: value.table, alias: (value.alias as string | null) ?? null };
}

function parseCriterion(value: unknown): CriterionExpr | null | undefined {
  if (value === null || value === undefined) return null;
  if (!isRecord(value) || typeof value.operator !== "string") return undefined; // undefined = invalid
  return { operator: value.operator as CriterionExpr["operator"], value: value.value };
}

function parseColumn(value: unknown): PlacedColumn | null {
  if (!isRecord(value)) return null;
  if (typeof value.tableRef !== "string" || typeof value.column !== "string") return null;
  if (typeof value.show !== "boolean" || typeof value.sort !== "string") return null;
  if (value.sortOrder != null && typeof value.sortOrder !== "number") return null;
  if (value.alias != null && typeof value.alias !== "string") return null;
  // The criteria array is read (.some/.length) inside a render-time memo — a
  // definition without it would throw a TypeError out of the render, so it is
  // rejected here rather than reaching setState.
  if (!Array.isArray(value.criteria)) return null;

  const criteria: (CriterionExpr | null)[] = [];
  for (const raw of value.criteria) {
    const criterion = parseCriterion(raw);
    if (criterion === undefined) return null;
    criteria.push(criterion);
  }

  return {
    tableRef: value.tableRef,
    column: value.column,
    show: value.show,
    sort: value.sort as PlacedColumn["sort"],
    sortOrder: (value.sortOrder as number | null) ?? null,
    alias: (value.alias as string | null) ?? null,
    criteria,
  };
}

function parseJoin(value: unknown): VisualJoin | null {
  if (!isRecord(value)) return null;
  if (typeof value.leftTable !== "string" || typeof value.rightTable !== "string") return null;
  if (!isStringArray(value.leftColumns) || !isStringArray(value.rightColumns)) return null;
  if (value.type !== "inner" && value.type !== "left") return null;
  return {
    leftTable: value.leftTable,
    leftColumns: value.leftColumns,
    rightTable: value.rightTable,
    rightColumns: value.rightColumns,
    type: value.type,
  };
}

/**
 * A filter node is either a group ('and'/'or' with children) or a leaf (with a
 * criterion); the tree is walked so a malformed node deep inside cannot slip
 * through into the designer (or the bridge) as a half-understood filter.
 */
function parseFilter(value: unknown): VisualFilter | null {
  if (!isRecord(value)) return null;
  if (value.op === "leaf") {
    const c = value.criterion;
    if (!isRecord(c) || typeof c.table !== "string" || typeof c.column !== "string" || typeof c.operator !== "string") {
      return null;
    }
    return {
      op: "leaf",
      children: null,
      criterion: {
        table: c.table,
        column: c.column,
        operator: c.operator as VisualCriterionOperator,
        value: c.value,
      },
    };
  }
  if (value.op !== "and" && value.op !== "or") return null;
  if (!Array.isArray(value.children) || value.children.length === 0) return null;

  const children: VisualFilter[] = [];
  for (const raw of value.children) {
    const child = parseFilter(raw);
    if (!child) return null;
    children.push(child);
  }
  return { op: value.op, children, criterion: null };
}

/**
 * Validates an untrusted `definition` (server JSON) as a saved visual query,
 * element by element — a container of the right shape holding a null table or a
 * column with no criteria array is NOT openable, and saying so here is what keeps
 * the "cannot open" message on screen instead of a TypeError from a render-time
 * memo. Returns null when the value is not a saved visual query, is a version this
 * build cannot open, or is internally malformed.
 */
export function parseQueryDefinition(value: unknown): SavedQueryDefinition | null {
  if (!isRecord(value)) return null;
  if (value.kind !== SAVED_QUERY_KIND || value.version !== SAVED_QUERY_VERSION) return null;

  const state = value.state;
  if (!isRecord(state)) return null;
  if (!Array.isArray(state.tables) || !Array.isArray(state.columns) || !Array.isArray(state.joins)) return null;
  if (state.rowLimit != null && typeof state.rowLimit !== "number") return null;

  const tables: PlacedTable[] = [];
  for (const raw of state.tables) {
    const table = parseTable(raw);
    if (!table) return null;
    tables.push(table);
  }

  const columns: PlacedColumn[] = [];
  for (const raw of state.columns) {
    const column = parseColumn(raw);
    if (!column) return null;
    columns.push(column);
  }

  const joins: VisualJoin[] = [];
  for (const raw of state.joins) {
    const join = parseJoin(raw);
    if (!join) return null;
    joins.push(join);
  }

  let filter: VisualFilter | null = null;
  if (state.filter != null) {
    filter = parseFilter(state.filter);
    if (!filter) return null;
  }

  return {
    kind: SAVED_QUERY_KIND,
    version: SAVED_QUERY_VERSION,
    state: { tables, columns, joins, filter, rowLimit: (state.rowLimit as number | null) ?? null },
  };
}

/**
 * Diffs a definition against the live schema, deriving the references from the
 * definition's state — so a design opened from an older build (or one whose
 * stored fingerprint was empty or stale) is judged on what it actually
 * references. Read-only: the definition is inspected, never rewritten — the
 * designer opens the query in degraded mode and shows the broken references
 * instead of guessing a fix.
 */
export function detectSchemaDrift(definition: SavedQueryDefinition, schema: BuilderSchema): SchemaDrift {
  const liveTables = new Set(schema.tables.map((t) => t.qualified));
  const liveColumns = new Set(schema.columns.map((c) => refKey(c.table, c.name)));
  const refs = queryRefs(definition.state);

  return {
    missingTables: refs.tables.filter((t) => !liveTables.has(t)),
    // A column of a dropped table is already covered by that table's entry.
    missingColumns: refs.columns.filter(
      (c) => liveTables.has(c.table) && !liveColumns.has(refKey(c.table, c.column))
    ),
  };
}
