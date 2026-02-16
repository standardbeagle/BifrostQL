import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { executeGraphQL } from './graphql-client';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

describe('executeGraphQL', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('sends POST request with correct content type', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL('http://localhost/graphql', {}, '{ users { id } }');

    const [url, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(url).toBe('http://localhost/graphql');
    expect(options.method).toBe('POST');
    expect(options.headers['Content-Type']).toBe('application/json');
  });

  it('includes query in request body', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL('http://localhost/graphql', {}, '{ users { id } }');

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body.query).toBe('{ users { id } }');
  });

  it('includes variables when provided', async () => {
    globalThis.fetch = createFetchMock({ data: { user: { id: 1 } } });

    await executeGraphQL(
      'http://localhost/graphql',
      {},
      'query GetUser($id: Int!) { user(id: $id) { id } }',
      { id: 42 },
    );

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body.variables).toEqual({ id: 42 });
  });

  it('omits variables when not provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL('http://localhost/graphql', {}, '{ users { id } }');

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body).not.toHaveProperty('variables');
  });

  it('omits variables when empty object provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL(
      'http://localhost/graphql',
      {},
      '{ users { id } }',
      {},
    );

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body).not.toHaveProperty('variables');
  });

  it('merges custom headers with content type', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL(
      'http://localhost/graphql',
      { Authorization: 'Bearer token', 'X-Custom': 'value' },
      '{ users { id } }',
    );

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(options.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer token',
      'X-Custom': 'value',
    });
  });

  it('returns data from successful response', async () => {
    const mockData = { users: [{ id: 1 }] };
    globalThis.fetch = createFetchMock({ data: mockData });

    const result = await executeGraphQL(
      'http://localhost/graphql',
      {},
      '{ users { id } }',
    );

    expect(result).toEqual(mockData);
  });

  it('throws on non-OK HTTP response', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    await expect(
      executeGraphQL('http://localhost/graphql', {}, '{ users { id } }'),
    ).rejects.toThrow('BifrostQL request failed: 500 Internal Server Error');
  });

  it('throws on 404 HTTP response', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 404,
      statusText: 'Not Found',
      json: () => Promise.resolve({}),
    });

    await expect(
      executeGraphQL('http://localhost/graphql', {}, '{ users { id } }'),
    ).rejects.toThrow('BifrostQL request failed: 404 Not Found');
  });

  it('throws with joined messages on GraphQL errors', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [{ message: 'Field not found' }, { message: 'Access denied' }],
    });

    await expect(
      executeGraphQL('http://localhost/graphql', {}, '{ users { id } }'),
    ).rejects.toThrow('Field not found, Access denied');
  });

  it('throws with single GraphQL error message', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [{ message: 'Table does not exist' }],
    });

    await expect(
      executeGraphQL('http://localhost/graphql', {}, '{ missing { id } }'),
    ).rejects.toThrow('Table does not exist');
  });

  it('throws when response contains no data', async () => {
    globalThis.fetch = createFetchMock({});

    await expect(
      executeGraphQL('http://localhost/graphql', {}, '{ users { id } }'),
    ).rejects.toThrow('BifrostQL response contained no data');
  });

  it('prefers error check over missing data', async () => {
    globalThis.fetch = createFetchMock({
      errors: [{ message: 'Parse error' }],
    });

    await expect(
      executeGraphQL('http://localhost/graphql', {}, 'invalid query'),
    ).rejects.toThrow('Parse error');
  });

  it('passes AbortSignal to fetch when provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });
    const controller = new AbortController();

    await executeGraphQL(
      'http://localhost/graphql',
      {},
      '{ users { id } }',
      undefined,
      controller.signal,
    );

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(options.signal).toBe(controller.signal);
  });

  it('does not include signal when not provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await executeGraphQL('http://localhost/graphql', {}, '{ users { id } }');

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(options.signal).toBeUndefined();
  });

  it('rejects with AbortError when signal is aborted', async () => {
    const controller = new AbortController();
    globalThis.fetch = vi.fn().mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          init.signal?.addEventListener('abort', () => {
            reject(
              new DOMException('The operation was aborted.', 'AbortError'),
            );
          });
        }),
    );

    const promise = executeGraphQL(
      'http://localhost/graphql',
      {},
      '{ users { id } }',
      undefined,
      controller.signal,
    );

    controller.abort();

    await expect(promise).rejects.toThrow('The operation was aborted.');
  });
});
