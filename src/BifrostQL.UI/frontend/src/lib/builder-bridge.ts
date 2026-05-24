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

export interface BuilderSchema {
  tables: BuilderTable[];
  columns: BuilderColumn[];
  relationships: BuilderRelationship[];
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
