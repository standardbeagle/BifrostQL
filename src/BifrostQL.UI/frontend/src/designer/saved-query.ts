/**
 * Serialization + schema-drift model for saved QBE queries.
 *
 * A saved query is stored as a `type: 'query'` saved object (the store from the
 * saved-object slice — the only storage path) whose `definition` is the JSON
 * emitted here: the designer state exactly as designed, plus a schema
 * fingerprint listing the real tables/columns the query depends on.
 *
 * The fingerprint is what makes drift detectable without re-deriving SQL: on
 * open we diff it against the live introspected schema and list the broken
 * references. Detection is read-only by contract — a query whose column was
 * dropped opens in degraded mode with a warning, and the stored definition is
 * never auto-rewritten (the user decides what the query should become).
 *
 * Pure module: no I/O, no React, no GraphQL text. The designer state it carries
 * only ever reaches the server through the existing VisualQuerySpec bridge, so
 * user-typed names stay validated identifiers, never spliced into query text.
 */

import type { VisualFilter } from "../lib/visual-query";
import type { BuilderSchema } from "../lib/builder-bridge";
import { tableRef, type DesignerState, type PlacedTable } from "./designer-state";

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
  fingerprint: SchemaFingerprint;
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
 * Projects the designer state into the persisted definition, deriving the
 * fingerprint from every live reference in the design: placed tables, grid
 * columns (shown, sorted, or filtered), join columns, and any directly-supplied
 * filter tree. Aliased refs (self-joins) resolve back to the real table name —
 * an alias is a designer-local token, not a schema object.
 */
export function serializeQuery(state: DesignerState): SavedQueryDefinition {
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
    kind: SAVED_QUERY_KIND,
    version: SAVED_QUERY_VERSION,
    state,
    fingerprint: {
      tables: [...new Set(state.tables.map((t) => t.table))].sort(),
      columns: [...columns].sort().map(splitRefKey),
    },
  };
}

/**
 * Validates an untrusted `definition` (server JSON) as a saved visual query.
 * Returns null when it is not one, or is a version this build cannot open — the
 * caller reports that rather than opening a half-understood design.
 */
export function parseQueryDefinition(value: unknown): SavedQueryDefinition | null {
  if (typeof value !== "object" || value === null) return null;
  const v = value as Record<string, unknown>;
  if (v.kind !== SAVED_QUERY_KIND || v.version !== SAVED_QUERY_VERSION) return null;

  const state = v.state as Partial<DesignerState> | null | undefined;
  if (typeof state !== "object" || state === null) return null;
  if (!Array.isArray(state.tables) || !Array.isArray(state.columns) || !Array.isArray(state.joins)) return null;

  const fingerprint = v.fingerprint as Partial<SchemaFingerprint> | null | undefined;
  if (typeof fingerprint !== "object" || fingerprint === null) return null;
  if (!Array.isArray(fingerprint.tables) || !Array.isArray(fingerprint.columns)) return null;

  return {
    kind: SAVED_QUERY_KIND,
    version: SAVED_QUERY_VERSION,
    state: {
      tables: state.tables,
      columns: state.columns,
      joins: state.joins,
      filter: state.filter ?? null,
      rowLimit: state.rowLimit ?? null,
    },
    fingerprint: { tables: fingerprint.tables, columns: fingerprint.columns },
  };
}

/**
 * Diffs a saved definition's fingerprint against the live schema. Read-only: the
 * definition is inspected, never rewritten — the designer opens the query in
 * degraded mode and shows the broken references instead of guessing a fix.
 */
export function detectSchemaDrift(definition: SavedQueryDefinition, schema: BuilderSchema): SchemaDrift {
  const liveTables = new Set(schema.tables.map((t) => t.qualified));
  const liveColumns = new Set(schema.columns.map((c) => refKey(c.table, c.name)));

  return {
    missingTables: definition.fingerprint.tables.filter((t) => !liveTables.has(t)),
    // A column of a dropped table is already covered by that table's entry.
    missingColumns: definition.fingerprint.columns.filter(
      (c) => liveTables.has(c.table) && !liveColumns.has(refKey(c.table, c.column))
    ),
  };
}
