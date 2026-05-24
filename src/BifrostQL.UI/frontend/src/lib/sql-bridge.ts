/**
 * Raw SQL execution over the Photino native bridge.
 *
 * This is a Photino-only channel: it runs SQL against the desktop host's
 * currently-active database connection entirely in-process via the `exec-sql`
 * bridge handler (see `Program.cs`). Nothing here touches the localhost
 * HTTP/GraphQL surface, so it works even when the GraphQL endpoint has no
 * connection bound, and credentials never cross a socket.
 *
 * Results come back columnar (columns + positional row arrays) so the grid can
 * preserve column order and tolerate duplicate / empty column names — neither
 * of which survives a name-keyed object shape.
 */

import {
  isBridgeAvailable,
  sendBridgeRequest,
  type BridgeRequestOptions,
} from "./native-bridge";

export interface SqlColumn {
  name: string;
  /** Provider data-type name (e.g. "int", "TEXT", "varchar"). May be empty. */
  type: string;
}

/**
 * A row is a positional array aligned to `columns`. Cell values are whatever
 * System.Text.Json produced from the DB value: string, number, boolean, null,
 * or (for dates/blobs/etc.) a string/base64 representation.
 */
export type SqlRow = unknown[];

export interface SqlResult {
  columns: SqlColumn[];
  rows: SqlRow[];
  /** Affected-row count for non-SELECT statements; provider-defined for SELECT. */
  rowsAffected: number;
  /** True when the result was capped at `maxRows` and more rows exist. */
  truncated: boolean;
}

export interface ExecSqlOptions {
  /** Named parameters bound as @name. Values: string | number | boolean | null. */
  params?: Record<string, string | number | boolean | null>;
  /** Cap on rows returned for result sets. Defaults to the host's value (1000). */
  maxRows?: number;
  /** Command timeout in seconds. Defaults to the host's value (30). */
  timeout?: number;
  /**
   * Bridge request timeout in ms. Defaults higher than the generic bridge
   * default because a SQL query can legitimately run for many seconds.
   */
  bridgeTimeoutMs?: number;
}

// A long query can outlast the 10s generic bridge default. Pad the bridge-side
// deadline well past the host command timeout so a slow-but-valid query reports
// a real DB timeout rather than a premature bridge timeout.
const DEFAULT_BRIDGE_TIMEOUT_MS = 60_000;

/** Whether the raw-SQL channel is usable (i.e. running inside Photino). */
export function isSqlBridgeAvailable(): boolean {
  return isBridgeAvailable();
}

/**
 * Executes SQL against the host's active connection. Rejects with a
 * `BridgeError` if the bridge is unavailable, the host has no connection,
 * the query errors, or the request times out.
 */
export function execSql(
  sql: string,
  options?: ExecSqlOptions
): Promise<SqlResult> {
  const payload = {
    sql,
    params: options?.params,
    maxRows: options?.maxRows,
    timeout: options?.timeout,
  };
  const bridgeOptions: BridgeRequestOptions = {
    timeoutMs: options?.bridgeTimeoutMs ?? DEFAULT_BRIDGE_TIMEOUT_MS,
  };
  return sendBridgeRequest<SqlResult>("exec-sql", payload, bridgeOptions);
}
