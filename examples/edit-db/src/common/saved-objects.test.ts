import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createSavedObjectsClient,
  parseSavedObject,
  SavedObjectConflictError,
  type SavedObject,
} from './saved-objects';

/** Builds a Response-like object for the mocked fetch. */
function jsonResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: `status ${status}`,
    json: async () => body,
  } as Response;
}

describe('saved-objects client', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockReset();
  });
  afterEach(() => vi.unstubAllGlobals());

  const client = createSavedObjectsClient();

  it('lists a type via GET /_saved-objects/{type}', async () => {
    const obj: SavedObject = { id: 'q1', type: 'query', name: 'Sales', definition: {}, version: 1 };
    fetchMock.mockResolvedValueOnce(jsonResponse(200, [obj]));

    const result = await client.list('query');

    expect(fetchMock).toHaveBeenCalledWith('/_saved-objects/query', expect.objectContaining({}));
    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('q1');
  });

  it('drops malformed items from a list', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(200, [
      { id: 'ok', type: 'form', name: 'n', definition: {}, version: 1 },
      { id: 'bad', type: 'not-a-type', name: 'n', definition: {}, version: 1 },
      { nope: true },
    ]));
    const result = await client.list();
    expect(result.map((o) => o.id)).toEqual(['ok']);
  });

  it('returns null when GET one is 404', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(404, { error: 'not found' }));
    expect(await client.get('query', 'missing')).toBeNull();
  });

  it('PUTs an object and returns the stored copy', async () => {
    const stored: SavedObject = { id: 'q1', type: 'query', name: 'Sales', definition: {}, version: 2 };
    fetchMock.mockResolvedValueOnce(jsonResponse(200, stored));

    const result = await client.put({ id: 'q1', type: 'query', name: 'Sales', definition: {}, version: 1 });

    const [, init] = fetchMock.mock.calls[0];
    expect(init).toMatchObject({ method: 'PUT', headers: { 'Content-Type': 'application/json' } });
    expect(result.version).toBe(2);
  });

  it('throws SavedObjectConflictError on a 409 PUT', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(409, { error: 'stale version' }));
    await expect(
      client.put({ id: 'q1', type: 'query', name: 'x', definition: {}, version: 1 })
    ).rejects.toBeInstanceOf(SavedObjectConflictError);
  });

  it('DELETE tolerates a 404', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(404, {}));
    await expect(client.remove('query', 'gone')).resolves.toBeUndefined();
  });

  it('resolves paths against a supplied baseUrl', async () => {
    const based = createSavedObjectsClient('https://api.example.com');
    fetchMock.mockResolvedValueOnce(jsonResponse(200, []));
    await based.list();
    expect(fetchMock).toHaveBeenCalledWith('https://api.example.com/_saved-objects', expect.anything());
  });
});

describe('parseSavedObject', () => {
  it('rejects non-conforming values', () => {
    expect(parseSavedObject(null)).toBeNull();
    expect(parseSavedObject({ id: 'x' })).toBeNull();
    expect(parseSavedObject({ id: 'x', type: 'bogus', name: 'n', definition: {}, version: 1 })).toBeNull();
    expect(parseSavedObject({ id: 'x', type: 'query', name: 'n', version: 1 })).toBeNull();
  });

  it('accepts a valid object and normalizes an absent folder to undefined', () => {
    const parsed = parseSavedObject({ id: 'x', type: 'query', name: 'n', definition: { a: 1 }, version: 3 });
    expect(parsed).not.toBeNull();
    expect(parsed!.folder).toBeUndefined();
    expect(parsed!.version).toBe(3);
  });
});
