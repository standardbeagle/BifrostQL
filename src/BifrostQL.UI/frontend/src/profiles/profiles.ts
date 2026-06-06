import type { ApiProfile } from './types';

export const PROFILES_ENDPOINT = '/api/profiles';

/**
 * Fallback used whenever the profiles endpoint is unreachable, returns a
 * non-ok status, or yields an empty list. The single raw-default entry maps
 * to `/graphql` with no `?profile=` query string.
 */
export const DEFAULT_PROFILES: ApiProfile[] = [
  { id: 'default', label: 'Database (raw)', serverProfile: null },
];

/**
 * GET the API profile list. The server (slice 6a) returns an array of
 * `{ id, label, serverProfile }` where the first entry is the raw default with
 * `serverProfile: null`. Any failure (network, non-ok, empty body) falls back
 * to {@link DEFAULT_PROFILES} so the picker always has at least one entry.
 */
export async function fetchProfiles(): Promise<ApiProfile[]> {
  try {
    const resp = await fetch(PROFILES_ENDPOINT);
    if (!resp.ok) return DEFAULT_PROFILES;
    const json = (await resp.json()) as ApiProfile[] | null | undefined;
    if (!Array.isArray(json) || json.length === 0) return DEFAULT_PROFILES;
    return json.map((p) => ({
      id: p.id,
      label: p.label,
      serverProfile: p.serverProfile ?? null,
    }));
  } catch {
    return DEFAULT_PROFILES;
  }
}

export const PROFILE_STORAGE_KEY = 'bifrost-ui:profile';

function safeStorage(storage?: Storage): Storage | null {
  if (storage) return storage;
  if (typeof window === 'undefined') return null;
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

export function loadActiveProfileId(storage?: Storage): string | null {
  const store = safeStorage(storage);
  if (!store) return null;
  try {
    return store.getItem(PROFILE_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function saveActiveProfileId(id: string, storage?: Storage): void {
  const store = safeStorage(storage);
  if (!store) return;
  try {
    store.setItem(PROFILE_STORAGE_KEY, id);
  } catch {
    // ignore quota / disabled-storage errors
  }
}

/**
 * Resolve the active profile from a list: the persisted id if it still exists,
 * otherwise the first profile.
 */
export function resolveActiveProfile(
  profiles: ApiProfile[],
  storage?: Storage,
): ApiProfile {
  const savedId = loadActiveProfileId(storage);
  const match = savedId ? profiles.find((p) => p.id === savedId) : undefined;
  return match ?? profiles[0];
}
