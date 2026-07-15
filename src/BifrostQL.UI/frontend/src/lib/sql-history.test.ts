import { describe, expect, it } from 'vitest';
import {
  SQL_HISTORY_CAPACITY,
  loadSqlHistory,
  recordSqlStatement,
} from './sql-history';

/** An in-memory Storage stand-in so the ring can be tested without a DOM. */
function fakeStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (k) => map.get(k) ?? null,
    key: (i) => [...map.keys()][i] ?? null,
    removeItem: (k) => map.delete(k),
    setItem: (k, v) => void map.set(k, v),
  } satisfies Storage;
}

describe('sql-history ring', () => {
  it('records newest-first', () => {
    // Arrange
    const store = fakeStorage();
    let t = 0;
    const now = () => ++t;
    // Act
    recordSqlStatement('SELECT 1', store, now);
    recordSqlStatement('SELECT 2', store, now);
    // Assert
    expect(loadSqlHistory(store).map((e) => e.sql)).toEqual(['SELECT 2', 'SELECT 1']);
  });

  it('ignores blank statements', () => {
    // Arrange
    const store = fakeStorage();
    // Act
    recordSqlStatement('   ', store);
    // Assert
    expect(loadSqlHistory(store)).toEqual([]);
  });

  it('does not pile up a re-run of the head statement', () => {
    // Arrange
    const store = fakeStorage();
    let t = 0;
    const now = () => ++t;
    // Act — same statement run twice in a row.
    recordSqlStatement('SELECT 1', store, now);
    const after = recordSqlStatement('SELECT 1', store, now);
    // Assert — one entry, timestamp refreshed to the latest run.
    expect(after).toHaveLength(1);
    expect(after[0].executedAt).toBe(2);
  });

  it('caps the ring at capacity', () => {
    // Arrange
    const store = fakeStorage();
    // Act — overflow the ring.
    for (let i = 0; i < SQL_HISTORY_CAPACITY + 10; i++) {
      recordSqlStatement(`SELECT ${i}`, store);
    }
    // Assert
    expect(loadSqlHistory(store)).toHaveLength(SQL_HISTORY_CAPACITY);
  });

  it('returns [] on corrupt persisted data', () => {
    // Arrange
    const store = fakeStorage();
    store.setItem('bifrostql_sql_history', 'not json');
    // Assert
    expect(loadSqlHistory(store)).toEqual([]);
  });
});
