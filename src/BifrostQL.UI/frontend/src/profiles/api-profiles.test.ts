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
} from './api-profiles';
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

/**
 * A profiles endpoint that is DOWN and a deployment that genuinely exposes
 * only the raw database used to be indistinguishable: both produced the canned
 * DEFAULT_PROFILES, so the picker greyed itself out with "this connection
 * exposes a single profile" and the user had no way to tell that the backend
 * had simply failed to answer. fetchProfiles now reports which of the two it
 * is, while still handing back a usable list either way.
 */
describe('fetchProfiles availability reporting', () => {
  it('reports a successful fetch as available', async () => {
    const payload: ApiProfile[] = [
      { id: 'default', label: 'Database (raw)', serverProfile: null },
      { id: 'sales', label: 'Sales (curated)', serverProfile: 'sales' },
    ];
    g.fetch.mockResolvedValue({ ok: true, json: async () => payload });

    const result = await fetchProfiles();
    expect(result.status).toBe('ok');
    expect(result.profiles).toHaveLength(2);
  });

  it('reports a non-ok response as unavailable, with a usable fallback list', async () => {
    g.fetch.mockResolvedValue({ ok: false, status: 503, json: async () => null });

    const result = await fetchProfiles();
    expect(result.status).toBe('unavailable');
    expect(result.profiles).toEqual(DEFAULT_PROFILES);
    if (result.status === 'unavailable') expect(result.reason).toContain('503');
  });

  it('reports a network failure as unavailable', async () => {
    g.fetch.mockRejectedValue(new Error('Failed to fetch'));

    const result = await fetchProfiles();
    expect(result.status).toBe('unavailable');
    expect(result.profiles).toEqual(DEFAULT_PROFILES);
    if (result.status === 'unavailable') expect(result.reason).toContain('Failed to fetch');
  });

  it('reports an empty or unusable payload as unavailable', async () => {
    g.fetch.mockResolvedValue({ ok: true, json: async () => [] });

    const result = await fetchProfiles();
    expect(result.status).toBe('unavailable');
    expect(result.profiles).toEqual(DEFAULT_PROFILES);
  });
});

describe('fetchProfiles', () => {
  it('maps a server payload to the ApiProfile shape', async () => {
    const payload: ApiProfile[] = [
      { id: 'default', label: 'Database (raw)', serverProfile: null },
      { id: 'sales', label: 'Sales (curated)', serverProfile: 'sales' },
    ];
    g.fetch.mockResolvedValue({ ok: true, json: async () => payload });

    await expect(fetchProfiles()).resolves.toEqual({ status: 'ok', profiles: payload });
    expect(g.fetch).toHaveBeenCalledWith(PROFILES_ENDPOINT);
  });

  it('returns DEFAULT_PROFILES on a 404 / non-ok response', async () => {
    g.fetch.mockResolvedValue({ ok: false, status: 404, json: async () => ({}) });
    await expect(fetchProfiles()).resolves.toMatchObject({ profiles: DEFAULT_PROFILES });
  });

  it('returns DEFAULT_PROFILES on an empty list', async () => {
    g.fetch.mockResolvedValue({ ok: true, json: async () => [] });
    await expect(fetchProfiles()).resolves.toMatchObject({ profiles: DEFAULT_PROFILES });
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

    await expect(fetchProfiles()).resolves.toEqual({
      status: 'ok',
      profiles: [
        { id: 'default', label: 'Database (raw)', serverProfile: null },
        { id: 'sales', label: 'Sales', serverProfile: 'sales' },
        { id: 'raw-ish', label: 'Raw-ish', serverProfile: null },
      ],
    });
  });

  it('returns DEFAULT_PROFILES when no server entries are valid', async () => {
    g.fetch.mockResolvedValue({
      ok: true,
      json: async () => [{ id: 'missing-label' }, { label: 'Missing id' }],
    });

    await expect(fetchProfiles()).resolves.toMatchObject({ profiles: DEFAULT_PROFILES });
  });

  it('returns DEFAULT_PROFILES when fetch throws', async () => {
    g.fetch.mockRejectedValue(new Error('network down'));
    await expect(fetchProfiles()).resolves.toMatchObject({ profiles: DEFAULT_PROFILES });
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
