/**
 * Visual query builder bridge — schema discovery.
 *
 * Photino-only channel: fetches the active connection's tables, columns, and FK
 * relationships from the host's cached DbModel via the `get-builder-schema`
 * handler, so the designer can populate its palette/canvas without any HTTP or
 * GraphQL call. Composite foreign keys arrive as parallel column arrays.
 *
 * The build-sql / build-and-exec wrappers (which turn a VisualQuerySpec into SQL
 * and run it) live alongside this in a later slice.
 */

import {
  isBridgeAvailable,
  sendBridgeRequest,
  type BridgeRequestOptions,
} from "./native-bridge";
import type { VisualQuerySpec } from "./visual-query";

export interface BuilderTable {
  schema: string;
  name: string;
  /** "schema.name" — the value VisualQuerySpec.tables[].table expects. */
  qualified: string;
}

export interface BuilderColumn {
  /** Qualified owning table ("schema.name"). */
  table: string;
  name: string;
  type: string;
  nullable: boolean;
  isPrimaryKey: boolean;
}

export interface BuilderRelationship {
  leftTable: string;
  /** Parallel to rightColumns — composite FKs list every pair. */
  leftColumns: string[];
  rightTable: string;
  rightColumns: string[];
}

/** A many-to-many bridge between two tables through a junction table. Column
 * lists are parallel for composite-key safety. Emitted in both directions by
 * the host (source↔target), so consumers should dedupe by junction. */
export interface BuilderManyToMany {
  sourceTable: string;
  sourceColumns: string[];
  junctionTable: string;
  junctionSourceColumns: string[];
  junctionTargetColumns: string[];
  targetTable: string;
  targetColumns: string[];
}

export interface BuilderSchema {
  tables: BuilderTable[];
  columns: BuilderColumn[];
  relationships: BuilderRelationship[];
  /** Many-to-many bridges. Optional: absent when talking to an older host. */
  manyToMany?: BuilderManyToMany[];
}

/** Whether the builder channel is usable (i.e. running inside Photino). */
export function isBuilderBridgeAvailable(): boolean {
  return isBridgeAvailable();
}

/**
 * Fetches the active connection's schema for the designer. Rejects with a
 * `BridgeError` if the bridge is unavailable, no connection is active, or the
 * model has not loaded yet.
 */
export function getBuilderSchema(options?: BridgeRequestOptions): Promise<BuilderSchema> {
  return sendBridgeRequest<BuilderSchema>("get-builder-schema", undefined, {
    timeoutMs: options?.timeoutMs ?? 30_000,
  });
}

/** Result of `buildSql`: the generated SQL and its named parameters (preview). */
export interface BuildSqlResult {
  sql: string;
  parameters: Record<string, unknown>;
}

/** Result of `buildAndExec`: the generated SQL plus the columnar result set. */
export interface BuildAndExecResult {
  sql: string;
  columns: { name: string; type: string }[];
  rows: unknown[][];
  rowsAffected: number;
  truncated: boolean;
}

// A built query can run for many seconds; pad the bridge deadline like execSql.
const DEFAULT_EXEC_TIMEOUT_MS = 60_000;

/**
 * Builds SQL from a spec without running it — for the designer's SQL-preview tab.
 * Rejects with `BridgeError` on validation/connection errors.
 */
export function buildSql(
  spec: VisualQuerySpec,
  options?: BridgeRequestOptions
): Promise<BuildSqlResult> {
  return sendBridgeRequest<BuildSqlResult>("build-sql", spec, {
    timeoutMs: options?.timeoutMs ?? 30_000,
  });
}

/**
 * Builds SQL from a spec and runs it, returning the columnar result set (plus the
 * SQL that ran) for the grid. Rejects with `BridgeError` on validation, connection,
 * or execution errors.
 */
export function buildAndExec(
  spec: VisualQuerySpec,
  options?: BridgeRequestOptions
): Promise<BuildAndExecResult> {
  return sendBridgeRequest<BuildAndExecResult>("build-and-exec", spec, {
    timeoutMs: options?.timeoutMs ?? DEFAULT_EXEC_TIMEOUT_MS,
  });
}
