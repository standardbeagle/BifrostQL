/**
 * SQLite file client for the host's `/api/sqlite/test` and `/api/sqlite/connect`.
 * The host builds (and quotes) the connection string and opens the file
 * `Mode=ReadWrite` unless `createNew` is set, so a mistyped path fails instead of
 * creating an empty database. Send a form `filePath`, or a raw / recent
 * `connectionString`, never both.
 */
export type SqliteFileTarget =
  | { filePath: string; createNew: boolean }
  | { connectionString: string };

export interface SqliteConnectResult {
  /** The connection string the host bound; store this one, it carries the Mode. */
  connectionString: string;
  database: string;
}

async function postSqlite<T>(path: string, target: SqliteFileTarget, failure: string): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(target),
  });
  const body = await response.json();
  if (!response.ok || !body.success) throw new Error(body.error || failure);
  return body as T;
}

/** Checks the file without binding it. A `createNew` target that does not exist yet passes. */
export async function testSqliteFile(target: SqliteFileTarget): Promise<string> {
  const body = await postSqlite<{ message: string }>('/api/sqlite/test', target, 'Connection test failed');
  return body.message;
}

/** Opens the file and binds it as the server's active connection. */
export async function connectSqliteFile(target: SqliteFileTarget): Promise<SqliteConnectResult> {
  return postSqlite<SqliteConnectResult>('/api/sqlite/connect', target, 'Connection failed');
}

/** The target a ConnectionRequest describes: form fields win, else the raw string. */
export function sqliteTargetOf(request: { filePath?: string; createNew?: boolean; connectionString?: string }): SqliteFileTarget | null {
  if (request.filePath) return { filePath: request.filePath, createNew: request.createNew ?? false };
  if (request.connectionString) return { connectionString: request.connectionString };
  return null;
}
