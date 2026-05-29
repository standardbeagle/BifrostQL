/**
 * Unit tests for active-session sessionStorage persistence.
 * Installs an in-memory sessionStorage shim on globalThis (Vitest node env).
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { saveSession, loadSession } from './session';

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
const KEY = 'bifrostql_active_session';
const info = { id: '1', name: 'prod', connectionString: 'cs', provider: 'postgres' };

beforeEach(() => { g.sessionStorage = memStorage(); });
afterEach(() => { delete g.sessionStorage; });

describe('session', () => {
  it('round-trips a saved session', () => {
    saveSession(info as any);
    expect(loadSession()).toEqual(info);
  });

  it('returns null when no session stored', () => {
    expect(loadSession()).toBeNull();
  });

  it('saving null clears the stored session', () => {
    saveSession(info as any);
    saveSession(null);
    expect(loadSession()).toBeNull();
    expect(g.sessionStorage.getItem(KEY)).toBeNull();
  });

  it('returns null on corrupt JSON instead of throwing', () => {
    g.sessionStorage.setItem(KEY, '{broken');
    expect(loadSession()).toBeNull();
  });
});
