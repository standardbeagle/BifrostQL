/**
 * Recent-history ring for executed SQL statements.
 *
 * This is the lightweight, always-available half of the console's history. The durable
 * saved-object `type: query` store (dependency 1.3) is the eventual home for named,
 * shareable queries; until that surface is wired end-to-end this ring gives the console a
 * local "what did I just run" recall without blocking on it. It persists to localStorage
 * so history survives a window reopen but stays on the desktop machine, never crossing a
 * wire — consistent with the console being a desktop-bridge-only, local-user feature.
 *
 * Entries are newest-first, capped at {@link SQL_HISTORY_CAPACITY}, and a re-run of the
 * statement already at the head is de-duplicated rather than piling up repeats.
 */

export interface SqlHistoryEntry {
  sql: string;
  /** Epoch milliseconds when the statement was executed. */
  executedAt: number;
}

export const SQL_HISTORY_KEY = 'bifrostql_sql_history';
export const SQL_HISTORY_CAPACITY = 50;

function defaultStorage(): Storage | null {
  return typeof localStorage === 'undefined' ? null : localStorage;
}

/** Reads the persisted history, newest-first. Returns [] on absent/corrupt data. */
export function loadSqlHistory(storage: Storage | null = defaultStorage()): SqlHistoryEntry[] {
  if (!storage) return [];
  try {
    const raw = storage.getItem(SQL_HISTORY_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (e): e is SqlHistoryEntry =>
        typeof e === 'object' &&
        e !== null &&
        typeof (e as SqlHistoryEntry).sql === 'string' &&
        typeof (e as SqlHistoryEntry).executedAt === 'number',
    );
  } catch {
    return [];
  }
}

/**
 * Records an executed statement at the head of the ring and returns the updated list.
 * Blank statements are ignored; a statement identical to the current head is not
 * duplicated (its timestamp is refreshed instead).
 */
export function recordSqlStatement(
  sql: string,
  storage: Storage | null = defaultStorage(),
  now: () => number = Date.now,
): SqlHistoryEntry[] {
  const trimmed = sql.trim();
  const existing = loadSqlHistory(storage);
  if (trimmed.length === 0) return existing;

  const withoutHeadDupe =
    existing.length > 0 && existing[0].sql === trimmed ? existing.slice(1) : existing;
  const next = [{ sql: trimmed, executedAt: now() }, ...withoutHeadDupe].slice(
    0,
    SQL_HISTORY_CAPACITY,
  );

  if (storage) {
    try {
      storage.setItem(SQL_HISTORY_KEY, JSON.stringify(next));
    } catch {
      // A full/blocked storage quota must not fail an otherwise-successful query run.
    }
  }
  return next;
}
