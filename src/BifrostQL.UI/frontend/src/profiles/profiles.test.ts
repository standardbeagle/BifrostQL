/**
 * Unit tests for the API profile helpers. Mocks globalThis.fetch so no network
 * or backend is required (Vitest node env).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  fetchProfiles,
  resolveActiveProfile,
  saveActiveProfileId,
  DEFAULT_PROFILES,
  PROFILES_ENDPOINT,
} from './profiles';
import type { ApiProfile } from './types';

const g = globalThis as any;

beforeEach(() => { g.fetch = vi.fn(); });
afterEach(() => { vi.restoreAllMocks(); delete g.fetch; });

/** Minimal in-memory Storage stand-in for the node test env. */
function memStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() { return map.size; },
    clear: () => map.clear(),
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    removeItem: (k: string) => { map.delete(k); },
    setItem: (k: string, v: string) => { map.set(k, v); },
  } as Storage;
}

describe('fetchProfiles', () => {
  it('maps a server payload to the ApiProfile shape', async () => {
    const payload: ApiProfile[] = [
      { id: 'default', label: 'Database (raw)', serverProfile: null },
      { id: 'sales', label: 'Sales (curated)', serverProfile: 'sales' },
    ];
    g.fetch.mockResolvedValue({ ok: true, json: async () => payload });

    await expect(fetchProfiles()).resolves.toEqual(payload);
    expect(g.fetch).toHaveBeenCalledWith(PROFILES_ENDPOINT);
  });

  it('returns DEFAULT_PROFILES on a 404 / non-ok response', async () => {
    g.fetch.mockResolvedValue({ ok: false, status: 404, json: async () => ({}) });
    await expect(fetchProfiles()).resolves.toEqual(DEFAULT_PROFILES);
  });

  it('returns DEFAULT_PROFILES on an empty list', async () => {
    g.fetch.mockResolvedValue({ ok: true, json: async () => [] });
    await expect(fetchProfiles()).resolves.toEqual(DEFAULT_PROFILES);
  });

  it('drops malformed profile entries from the server payload', async () => {
    g.fetch.mockResolvedValue({
      ok: true,
      json: async () => [
        { id: 'default', label: 'Database (raw)', serverProfile: null },
        { id: 'bad-label', serverProfile: 'bad' },
        { label: 'Missing id', serverProfile: 'bad' },
        { id: 'sales', label: 'Sales', serverProfile: 'sales' },
        { id: 'raw-ish', label: 'Raw-ish', serverProfile: 42 },
      ],
    });

    await expect(fetchProfiles()).resolves.toEqual([
      { id: 'default', label: 'Database (raw)', serverProfile: null },
      { id: 'sales', label: 'Sales', serverProfile: 'sales' },
      { id: 'raw-ish', label: 'Raw-ish', serverProfile: null },
    ]);
  });

  it('returns DEFAULT_PROFILES when no server entries are valid', async () => {
    g.fetch.mockResolvedValue({
      ok: true,
      json: async () => [{ id: 'missing-label' }, { label: 'Missing id' }],
    });

    await expect(fetchProfiles()).resolves.toEqual(DEFAULT_PROFILES);
  });

  it('returns DEFAULT_PROFILES when fetch throws', async () => {
    g.fetch.mockRejectedValue(new Error('network down'));
    await expect(fetchProfiles()).resolves.toEqual(DEFAULT_PROFILES);
  });
});

describe('resolveActiveProfile', () => {
  const profiles: ApiProfile[] = [
    { id: 'default', label: 'Database (raw)', serverProfile: null },
    { id: 'sales', label: 'Sales (curated)', serverProfile: 'sales' },
  ];

  it('picks the persisted profile id when it still exists', () => {
    const storage = memStorage();
    saveActiveProfileId('sales', storage);
    expect(resolveActiveProfile(profiles, storage)).toEqual(profiles[1]);
  });

  it('falls back to the first profile when nothing is persisted', () => {
    const storage = memStorage();
    expect(resolveActiveProfile(profiles, storage)).toEqual(profiles[0]);
  });

  it('falls back to the first profile when the persisted id is gone', () => {
    const storage = memStorage();
    saveActiveProfileId('removed', storage);
    expect(resolveActiveProfile(profiles, storage)).toEqual(profiles[0]);
  });
});
