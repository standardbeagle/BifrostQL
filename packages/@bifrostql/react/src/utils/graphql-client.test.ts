import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { executeGraphQL, BifrostAuthError } from './graphql-client';

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

  describe('auth refresh / session-failure handling', () => {
    function createAuthFailingThenOkFetch(okResponse: unknown) {
      // First call: 401. Second call (retry): OK.
      return vi
        .fn()
        .mockResolvedValueOnce({
          ok: false,
          status: 401,
          statusText: 'Unauthorized',
          json: () => Promise.resolve({}),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve(okResponse),
        });
    }

    it('refreshes and retries once on HTTP 401, then succeeds', async () => {
      globalThis.fetch = createAuthFailingThenOkFetch({
        data: { users: [{ id: 1 }] },
      });
      const refreshToken = vi.fn().mockResolvedValue('fresh-token');
      let tokenCalls = 0;
      const getToken = vi.fn(() => {
        tokenCalls += 1;
        return tokenCalls === 1 ? 'stale-token' : 'fresh-token';
      });

      const result = await executeGraphQL(
        'http://localhost/graphql',
        {},
        '{ users { id } }',
        undefined,
        undefined,
        getToken,
        { refreshToken },
      );

      expect(result).toEqual({ users: [{ id: 1 }] });
      expect(refreshToken).toHaveBeenCalledTimes(1);
      expect(globalThis.fetch).toHaveBeenCalledTimes(2);
      const [, retryOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[1];
      expect(retryOptions.headers['Authorization']).toBe('Bearer fresh-token');
    });

    it('uses the token returned by refreshToken on the retry', async () => {
      globalThis.fetch = createAuthFailingThenOkFetch({ data: { ok: true } });
      const refreshToken = vi.fn().mockResolvedValue('returned-token');

      await executeGraphQL(
        'http://localhost/graphql',
        {},
        '{ users { id } }',
        undefined,
        undefined,
        () => 'stale-token',
        { refreshToken },
      );

      const [, retryOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>)
        .mock.calls[1];
      expect(retryOptions.headers['Authorization']).toBe(
        'Bearer returned-token',
      );
    });

    it('refreshes and retries once on a GraphQL auth error', async () => {
      globalThis.fetch = vi
        .fn()
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () =>
            Promise.resolve({
              data: null,
              errors: [{ message: 'Unauthorized: token expired' }],
            }),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { users: [] } }),
        });
      const refreshToken = vi.fn().mockResolvedValue('fresh-token');

      const result = await executeGraphQL(
        'http://localhost/graphql',
        {},
        '{ users { id } }',
        undefined,
        undefined,
        () => 'stale-token',
        { refreshToken },
      );

      expect(result).toEqual({ users: [] });
      expect(refreshToken).toHaveBeenCalledTimes(1);
    });

    it('throws BifrostAuthError when no refreshToken hook is configured', async () => {
      globalThis.fetch = createFetchMock({}, false, 401);

      await expect(
        executeGraphQL('http://localhost/graphql', {}, '{ users { id } }'),
      ).rejects.toBeInstanceOf(BifrostAuthError);
    });

    it('throws BifrostAuthError when refreshToken hook throws', async () => {
      globalThis.fetch = createFetchMock({}, false, 401);
      const refreshToken = vi
        .fn()
        .mockRejectedValue(new Error('refresh endpoint down'));

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          () => 'stale-token',
          { refreshToken },
        ),
      ).rejects.toBeInstanceOf(BifrostAuthError);
    });

    it('throws BifrostAuthError when the retry also fails with 401', async () => {
      globalThis.fetch = createFetchMock({}, false, 401);
      const refreshToken = vi.fn().mockResolvedValue('still-bad-token');

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          () => 'stale-token',
          { refreshToken },
        ),
      ).rejects.toBeInstanceOf(BifrostAuthError);
      // One initial + one retry only — never more than one retry.
      expect(globalThis.fetch).toHaveBeenCalledTimes(2);
    });

    it('invokes onSessionExpired with the typed error when refresh fails', async () => {
      globalThis.fetch = createFetchMock({}, false, 401);
      const onSessionExpired = vi.fn();

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          undefined,
          { onSessionExpired },
        ),
      ).rejects.toBeInstanceOf(BifrostAuthError);
      expect(onSessionExpired).toHaveBeenCalledTimes(1);
      expect(onSessionExpired.mock.calls[0][0]).toBeInstanceOf(
        BifrostAuthError,
      );
    });

    it('does not invoke onSessionExpired when the retry succeeds', async () => {
      globalThis.fetch = createAuthFailingThenOkFetch({ data: { users: [] } });
      const onSessionExpired = vi.fn();
      const refreshToken = vi.fn().mockResolvedValue('fresh-token');

      await executeGraphQL(
        'http://localhost/graphql',
        {},
        '{ users { id } }',
        undefined,
        undefined,
        () => 'stale',
        { refreshToken, onSessionExpired },
      );

      expect(onSessionExpired).not.toHaveBeenCalled();
    });

    it('does not retry or alter behavior on a non-auth GraphQL error', async () => {
      globalThis.fetch = createFetchMock({
        data: null,
        errors: [{ message: 'Field not found' }],
      });
      const refreshToken = vi.fn();

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          () => 'token',
          { refreshToken },
        ),
      ).rejects.toThrow('Field not found');
      expect(refreshToken).not.toHaveBeenCalled();
      expect(globalThis.fetch).toHaveBeenCalledTimes(1);
    });

    it('does not treat a "forbidden" (403 authorization) GraphQL error as an auth failure', async () => {
      // A 403-style permission denial is NOT an authentication failure: the
      // token is valid but lacks permission. It must not trigger token refresh
      // or session expiry.
      globalThis.fetch = createFetchMock({
        data: null,
        errors: [{ message: 'Forbidden: insufficient permissions' }],
      });
      const refreshToken = vi.fn();
      const onSessionExpired = vi.fn();

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          () => 'token',
          { refreshToken, onSessionExpired },
        ),
      ).rejects.toThrow('Forbidden: insufficient permissions');
      expect(refreshToken).not.toHaveBeenCalled();
      expect(onSessionExpired).not.toHaveBeenCalled();
      expect(globalThis.fetch).toHaveBeenCalledTimes(1);
    });

    it('does not retry on a non-401 HTTP error', async () => {
      globalThis.fetch = createFetchMock({}, false, 500);
      const refreshToken = vi.fn();

      await expect(
        executeGraphQL(
          'http://localhost/graphql',
          {},
          '{ users { id } }',
          undefined,
          undefined,
          () => 'token',
          { refreshToken },
        ),
      ).rejects.toThrow('BifrostQL request failed: 500');
      expect(refreshToken).not.toHaveBeenCalled();
      expect(globalThis.fetch).toHaveBeenCalledTimes(1);
    });
  });

  it('rejects with AbortError when signal is aborted', async () => {
    const controller = new AbortController();
    globalThis.fetch = vi.fn().mockImplementation(
      (_url: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          const abortError = new DOMException(
            'The operation was aborted.',
            'AbortError',
          );
          if (init.signal?.aborted) {
            reject(abortError);
            return;
          }
          init.signal?.addEventListener('abort', () => {
            reject(abortError);
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
