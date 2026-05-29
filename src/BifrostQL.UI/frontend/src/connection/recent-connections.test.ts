/**
 * Unit tests for recent-connections localStorage persistence.
 *
 * Runs in Vitest's default node environment (no jsdom), so we install a small
 * in-memory localStorage + window shim on globalThis, mirroring how the
 * native-bridge tests shim window.external.
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  saveRecentConnections,
  loadRecentConnections,
  MAX_RECENT_CONNECTIONS,
} from './recent-connections';

function memStorage(): Storage {
  const m = new Map<string, string>();
  return {
    getItem: (k) => (m.has(k) ? m.get(k)! : null),
    setItem: (k, v) => void m.set(k, String(v)),
    removeItem: (k) => void m.delete(k),
    clear: () => m.clear(),
    key: (i) => [...m.keys()][i] ?? null,
    get length() { return m.size; },
  } as Storage;
}

const g = globalThis as any;
const KEY = 'bifrostql_recent_connections';
const conn = (name: string) => ({ id: name, name, connectionString: `cs-${name}`, provider: 'sqlite' });

beforeEach(() => {
  g.window = {};
  g.localStorage = memStorage();
});
afterEach(() => {
  delete g.window;
  delete g.localStorage;
});

describe('recent-connections', () => {
  it('round-trips saved connections', () => {
    const items = [conn('a'), conn('b')];
    saveRecentConnections(items as any);
    expect(loadRecentConnections()).toEqual(items);
  });

  it('returns empty array when nothing stored', () => {
    expect(loadRecentConnections()).toEqual([]);
  });

  it('caps loaded connections at MAX_RECENT_CONNECTIONS', () => {
    const seven = Array.from({ length: 7 }, (_, i) => conn(`c${i}`));
    saveRecentConnections(seven as any);
    expect(loadRecentConnections()).toHaveLength(MAX_RECENT_CONNECTIONS);
  });

  it('returns empty array on corrupt JSON', () => {
    g.localStorage.setItem(KEY, '{not valid json');
    expect(loadRecentConnections()).toEqual([]);
  });

  it('is a no-op without a window (SSR guard)', () => {
    delete g.window;
    expect(() => saveRecentConnections([conn('x')] as any)).not.toThrow();
    expect(loadRecentConnections()).toEqual([]);
  });
});
