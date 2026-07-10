/**
 * Client for the server's `/_saved-objects` REST endpoint (list/get/put/delete of
 * user-authored objects). A small REST helper — the GraphQL fetcher seam is
 * GraphQL-only, so this is a sibling transport, not an extension of it. Paths are
 * resolved against `baseUrl` (default `''` = the page origin, like the other
 * `/api/*` REST clients).
 *
 * Types are declared here to keep this shipped stack self-contained; they mirror
 * the canonical contract in `@bifrostql/types` (`SavedObject`) and the C# source of
 * truth in `BifrostQL.Core/SavedObjects`.
 */

export type SavedObjectType = 'query' | 'form' | 'report' | 'dashboard';

export interface SavedObject {
  id: string;
  type: SavedObjectType;
  name: string;
  folder?: string;
  definition: unknown;
  version: number;
}

const SAVED_OBJECT_TYPES: readonly SavedObjectType[] = ['query', 'form', 'report', 'dashboard'];

/** Thrown when a `put` is rejected because the object was modified concurrently (HTTP 409). */
export class SavedObjectConflictError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SavedObjectConflictError';
  }
}

export interface SavedObjectsClient {
  list(type?: SavedObjectType, signal?: AbortSignal): Promise<SavedObject[]>;
  get(type: SavedObjectType, id: string, signal?: AbortSignal): Promise<SavedObject | null>;
  put(object: SavedObject, signal?: AbortSignal): Promise<SavedObject>;
  remove(type: SavedObjectType, id: string, signal?: AbortSignal): Promise<void>;
}

export const SAVED_OBJECTS_PATH = '/_saved-objects';

export function createSavedObjectsClient(baseUrl = ''): SavedObjectsClient {
  const root = `${baseUrl}${SAVED_OBJECTS_PATH}`;
  const enc = encodeURIComponent;

  async function readError(resp: Response): Promise<string> {
    try {
      const body = (await resp.json()) as { error?: string };
      return body?.error ?? `${resp.status} ${resp.statusText}`;
    } catch {
      return `${resp.status} ${resp.statusText}`;
    }
  }

  return {
    async list(type, signal) {
      const url = type ? `${root}/${enc(type)}` : root;
      const resp = await fetch(url, { signal });
      if (!resp.ok) throw new Error(`Failed to list saved objects: ${await readError(resp)}`);
      const json: unknown = await resp.json();
      return Array.isArray(json) ? json.map(parseSavedObject).filter((o): o is SavedObject => o !== null) : [];
    },

    async get(type, id, signal) {
      const resp = await fetch(`${root}/${enc(type)}/${enc(id)}`, { signal });
      if (resp.status === 404) return null;
      if (!resp.ok) throw new Error(`Failed to load saved object: ${await readError(resp)}`);
      return parseSavedObject(await resp.json());
    },

    async put(object, signal) {
      const resp = await fetch(`${root}/${enc(object.type)}/${enc(object.id)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(object),
        signal,
      });
      if (resp.status === 409) throw new SavedObjectConflictError(await readError(resp));
      if (!resp.ok) throw new Error(`Failed to save object: ${await readError(resp)}`);
      const saved = parseSavedObject(await resp.json());
      if (saved === null) throw new Error('Server returned a malformed saved object.');
      return saved;
    },

    async remove(type, id, signal) {
      const resp = await fetch(`${root}/${enc(type)}/${enc(id)}`, { method: 'DELETE', signal });
      if (!resp.ok && resp.status !== 404) throw new Error(`Failed to delete saved object: ${await readError(resp)}`);
    },
  };
}

/** Validates an unknown value as a {@link SavedObject}; returns null when it does not conform. */
export function parseSavedObject(value: unknown): SavedObject | null {
  if (typeof value !== 'object' || value === null) return null;
  const v = value as Record<string, unknown>;
  if (typeof v.id !== 'string' || v.id.length === 0) return null;
  if (typeof v.type !== 'string' || !SAVED_OBJECT_TYPES.includes(v.type as SavedObjectType)) return null;
  if (typeof v.name !== 'string') return null;
  if (typeof v.version !== 'number') return null;
  if (!('definition' in v)) return null;
  return {
    id: v.id,
    type: v.type as SavedObjectType,
    name: v.name,
    folder: typeof v.folder === 'string' ? v.folder : undefined,
    definition: v.definition,
    version: v.version,
  };
}
