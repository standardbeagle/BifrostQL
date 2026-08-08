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
 * Outcome of a profile fetch. Both variants carry a usable `profiles` list so
 * the app never has to special-case an empty picker, but the caller can still
 * tell the two apart — a backend that failed to answer is not the same thing
 * as a deployment that genuinely exposes only the raw database, and rendering
 * them identically left the user with no way to know the difference.
 */
export type ProfilesResult =
  | { status: 'ok'; profiles: ApiProfile[] }
  | { status: 'unavailable'; profiles: ApiProfile[]; reason: string };

/**
 * GET the API profile list. The server (slice 6a) returns an array of
 * `{ id, label, serverProfile }` where the first entry is the raw default with
 * `serverProfile: null`. On any failure — network, non-ok, or a body carrying
 * no usable entry — the result is reported as `unavailable` alongside
 * {@link DEFAULT_PROFILES}, so the picker always has an entry AND can say that
 * the list it is showing is a fallback rather than the server's answer.
 */
export async function fetchProfiles(): Promise<ProfilesResult> {
  const unavailable = (reason: string): ProfilesResult => ({
    status: 'unavailable',
    profiles: DEFAULT_PROFILES,
    reason,
  });

  try {
    const resp = await fetch(PROFILES_ENDPOINT);
    if (!resp.ok) {
      return unavailable(`Profiles endpoint returned ${resp.status}.`);
    }
    const json = (await resp.json()) as ApiProfile[] | null | undefined;
    if (!Array.isArray(json) || json.length === 0) {
      return unavailable('Profiles endpoint returned no entries.');
    }
    const profiles = json
      .map(parseApiProfile)
      .filter((profile): profile is ApiProfile => profile !== null);
    if (profiles.length === 0) {
      return unavailable('Profiles endpoint returned no usable entries.');
    }
    return { status: 'ok', profiles };
  } catch (error) {
    return unavailable(
      `Profiles endpoint unreachable: ${error instanceof Error ? error.message : String(error)}`,
    );
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

function parseApiProfile(value: unknown): ApiProfile | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return null;
  }

  const profile = value as Record<string, unknown>;
  if (typeof profile.id !== 'string' || typeof profile.label !== 'string') {
    return null;
  }

  return {
    id: profile.id,
    label: profile.label,
    serverProfile:
      typeof profile.serverProfile === 'string' ? profile.serverProfile : null,
  };
}
