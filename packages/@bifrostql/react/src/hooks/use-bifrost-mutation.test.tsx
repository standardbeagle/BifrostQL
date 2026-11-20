import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, render, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostMutation } from './use-bifrost-mutation';
import { BifrostGraphQLError } from '../utils/graphql-client';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

function createWrapper(
  endpoint = 'http://localhost:5000/graphql',
  headers?: Record<string, string>,
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint, headers }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

describe('useBifrostMutation', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('throws when used outside BifrostProvider', () => {
    const queryClient = new QueryClient({
      defaultOptions: { mutations: { retry: false } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          {children}
        </QueryClientProvider>
      );
    }

    expect(() => {
      renderHook(
        () =>
          useBifrostMutation(
            'mutation Insert($detail: Insert_users) { users(insert: $detail) }',
          ),
        { wrapper: Wrapper },
      );
    }).toThrow('useBifrostMutation must be used within a BifrostProvider');
  });

  it('executes an insert mutation', async () => {
    const mockData = { users: 1 };
    globalThis.fetch = createFetchMock({ data: mockData });

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(),
    });

    act(() => {
      result.current.mutate({ detail: { name: 'Alice' } });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(mockData);
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it('sends mutation and variables in request body', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const mutation =
      'mutation Update($detail: Update_users) { users(update: $detail) }';
    const variables = { detail: { id: 1, name: 'Bob' } };

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(variables);
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toBe(mutation);
    expect(body.variables).toEqual(variables);
  });

  it('sends configured headers with request', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const wrapper = createWrapper('http://localhost/graphql', {
      Authorization: 'Bearer test-token',
    });

    const mutation =
      'mutation Delete($detail: Delete_users) { users(delete: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper,
    });

    await act(async () => {
      await result.current.mutateAsync({ detail: { id: 1 } });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(fetchOptions.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer test-token',
      'Idempotency-Key': expect.stringMatching(/^[0-9a-f-]{36}$/),
    });
  });

  it('throws on HTTP error response', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(),
    });

    act(() => {
      result.current.mutate({ detail: { name: 'Alice' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toContain('500');
  });

  it('gives the mutation result and onError the server error code', async () => {
    globalThis.fetch = createFetchMock({
      data: { users: null },
      errors: [{ message: 'Taken', extensions: { code: 'CONFLICT' } }],
    });
    const onError = vi.fn();

    const { result } = renderHook(
      () =>
        useBifrostMutation(
          'mutation Insert($detail: Insert_users) { users(insert: $detail) }',
          { onError },
        ),
      { wrapper: createWrapper() },
    );

    act(() => {
      result.current.mutate({ detail: { name: 'a' } });
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });
    expect(result.current.error).toBeInstanceOf(BifrostGraphQLError);
    expect((result.current.error as BifrostGraphQLError).code).toBe('CONFLICT');
    expect((onError.mock.calls[0][0] as BifrostGraphQLError).code).toBe(
      'CONFLICT',
    );
  });

  it('throws on GraphQL errors in response', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [{ message: 'Validation failed' }, { message: 'Missing field' }],
    });

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(),
    });

    act(() => {
      result.current.mutate({ detail: { name: '' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe(
      'Validation failed, Missing field',
    );
  });

  it('calls onSuccess callback after mutation', async () => {
    const mockData = { users: 1 };
    globalThis.fetch = createFetchMock({ data: mockData });

    const onSuccess = vi.fn();
    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(
      () => useBifrostMutation(mutation, { onSuccess }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({ detail: { name: 'Alice' } });
    });

    expect(onSuccess).toHaveBeenCalledWith(mockData);
  });

  it('calls onError callback on failure', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const onError = vi.fn();
    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(
      () => useBifrostMutation(mutation, { onError }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      try {
        await result.current.mutateAsync({ detail: { name: 'Alice' } });
      } catch {
        // expected
      }
    });

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0].message).toContain('500');
  });

  it('uses correct endpoint from config', async () => {
    const customEndpoint = 'https://api.example.com/graphql';
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(customEndpoint),
    });

    await act(async () => {
      await result.current.mutateAsync({ detail: { name: 'Alice' } });
    });

    const [url] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe(customEndpoint);
  });

  it('invalidates specified queries on success', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, gcTime: 0 },
        mutations: { retry: false },
      },
    });

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider
            config={{ endpoint: 'http://localhost:5000/graphql' }}
          >
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    }

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(
      () =>
        useBifrostMutation(mutation, {
          invalidateQueries: ['{ users { id } }', '{ users { id name } }'],
        }),
      { wrapper: Wrapper },
    );

    await act(async () => {
      await result.current.mutateAsync({ detail: { name: 'Alice' } });
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', 'http://localhost:5000/graphql', '{ users { id } }'],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', 'http://localhost:5000/graphql', '{ users { id name } }'],
    });
  });

  it('invalidates cached queries by table name, not just exact key prefix', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false },
      },
    });

    // Bifrost queries are keyed ['bifrost', endpoint, <full query string>,
    // vars] — a bare table name used as a key prefix matches nothing, so the
    // table-name form must find this entry through the query text.
    const usersText =
      '{\n  users {\n    total\n    data {\n      id\n    }\n  }\n}';
    const usersKey = ['bifrost', 'http://localhost:5000/graphql', usersText, {}];
    const ordersKey = [
      'bifrost',
      'http://localhost:5000/graphql',
      '{\n  orders {\n    total\n    data {\n      id\n    }\n  }\n}',
      {},
    ];
    // Same query text on another provider's endpoint: a write here must not
    // invalidate that tenant's cache.
    const otherEndpointUsersKey = [
      'bifrost',
      'http://other-tenant/graphql',
      usersText,
      {},
    ];
    queryClient.setQueryData(usersKey, { users: { total: 0, data: [] } });
    queryClient.setQueryData(ordersKey, { orders: { total: 0, data: [] } });
    queryClient.setQueryData(otherEndpointUsersKey, {
      users: { total: 0, data: [] },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider
            config={{ endpoint: 'http://localhost:5000/graphql' }}
          >
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    }

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(
      () => useBifrostMutation(mutation, { invalidateQueries: ['users'] }),
      { wrapper: Wrapper },
    );

    await act(async () => {
      await result.current.mutateAsync({ detail: { name: 'Alice' } });
    });

    expect(queryClient.getQueryState(usersKey)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(ordersKey)?.isInvalidated).toBe(false);
    expect(
      queryClient.getQueryState(otherEndpointUsersKey)?.isInvalidated,
    ).toBe(false);
  });

  it('provides standard mutation state properties', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const mutation =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';

    const { result } = renderHook(() => useBifrostMutation(mutation), {
      wrapper: createWrapper(),
    });

    expect(result.current.isIdle).toBe(true);
    expect(result.current.isPending).toBe(false);
    expect(result.current.isSuccess).toBe(false);
    expect(result.current.isError).toBe(false);

    act(() => {
      result.current.mutate({ detail: { name: 'Alice' } });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.isIdle).toBe(false);
  });

  describe('Idempotency-Key', () => {
    const insert =
      'mutation Insert($detail: Insert_users) { users(insert: $detail) }';
    const answered = () => ({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data: { users: 1 } }),
    });
    const graphQlError = () => ({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () =>
        Promise.resolve({
          errors: [{ message: 'refused', extensions: { code: 'CONFLICT' } }],
        }),
    });
    const serverError = () => ({
      ok: false,
      status: 502,
      statusText: 'Bad Gateway',
      json: () => Promise.reject(new Error('no body')),
    });

    /** The Idempotency-Key header each fetch call sent, in order. */
    function sentKeys(fetchMock: ReturnType<typeof vi.fn>): string[] {
      return fetchMock.mock.calls.map(
        (call) =>
          (call[1] as { headers: Record<string, string> }).headers[
            'Idempotency-Key'
          ],
      );
    }

    async function settle(promise: Promise<unknown>) {
      await act(async () => {
        await promise.catch(() => undefined);
      });
    }

    it('sends a new Idempotency-Key with each call', async () => {
      const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;
      const { result } = renderHook(() => useBifrostMutation(insert), {
        wrapper: createWrapper(),
      });

      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));

      const [first, second] = sentKeys(fetchMock);
      expect(first).toMatch(/^[0-9a-f-]{36}$/);
      expect(second).toMatch(/^[0-9a-f-]{36}$/);
      expect(second).not.toBe(first);
    });

    it('sends the same key when the instance repeats its unanswered call', async () => {
      const fetchMock = vi
        .fn()
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;
      const { result } = renderHook(() => useBifrostMutation(insert), {
        wrapper: createWrapper(),
      });

      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));

      const [first, retry] = sentKeys(fetchMock);
      expect(first).toMatch(/^[0-9a-f-]{36}$/);
      expect(retry).toBe(first);
    });

    it("sends the same key when the instance's next call has other variables", async () => {
      const fetchMock = vi
        .fn()
        .mockResolvedValueOnce(serverError())
        .mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;
      const { result } = renderHook(() => useBifrostMutation(insert), {
        wrapper: createWrapper(),
      });

      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.mutateAsync({ detail: { name: 'corrected' } }));

      const [first, retry] = sentKeys(fetchMock);
      expect(first).toMatch(/^[0-9a-f-]{36}$/);
      expect(retry).toBe(first);
    });

    it('draws a new key once a call is answered', async () => {
      const fetchMock = vi
        .fn()
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockResolvedValueOnce(graphQlError())
        .mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;
      const { result } = renderHook(() => useBifrostMutation(insert), {
        wrapper: createWrapper(),
      });

      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.mutateAsync({ detail: { name: 'a' } }));

      const [first, retry, next] = sentKeys(fetchMock);
      expect(retry).toBe(first);
      expect(next).not.toBe(retry);
    });

    it("keeps an unanswered call's key while another instance's calls are answered", async () => {
      const fetchMock = vi
        .fn()
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockResolvedValueOnce(answered())
        .mockResolvedValueOnce(answered())
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;
      const { result } = renderHook(
        () => ({ a: useBifrostMutation(insert), b: useBifrostMutation(insert) }),
        { wrapper: createWrapper() },
      );

      await settle(result.current.a.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.b.mutateAsync({ detail: { name: 'b' } }));
      await settle(result.current.b.mutateAsync({ detail: { name: 'b' } }));
      // A's same call, still unanswered (the fetch fails again), then a corrected one.
      await settle(result.current.a.mutateAsync({ detail: { name: 'a' } }));
      await settle(result.current.a.mutateAsync({ detail: { name: 'corrected' } }));

      const [aFirst, bFirst, bSecond, aSame, aCorrected] = sentKeys(fetchMock);
      expect(aFirst).toMatch(/^[0-9a-f-]{36}$/);
      expect(bFirst).not.toBe(aFirst);
      expect(bSecond).not.toBe(aFirst);
      expect(aSame).toBe(aFirst);
      expect(aCorrected).toBe(aFirst);
    });

    it('forgets an unanswered call on reset and on unmount', async () => {
      const fetchMock = vi
        .fn()
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockImplementation(() => Promise.resolve(answered()));
      globalThis.fetch = fetchMock;

      let hook: ReturnType<typeof useBifrostMutation> | undefined;
      function Probe() {
        hook = useBifrostMutation(insert);
        return null;
      }
      const Wrapper = createWrapper();
      const view = render(
        <Wrapper>
          <Probe key="first" />
        </Wrapper>,
      );

      // reset() forgets the unanswered call.
      await settle(hook!.mutateAsync({ detail: { name: 'a' } }));
      await waitFor(() => expect(hook!.error).not.toBeNull());
      act(() => hook!.reset());
      await waitFor(() => expect(hook!.error).toBeNull());
      await settle(hook!.mutateAsync({ detail: { name: 'a' } }));

      // The second call failed unanswered too; a remount under the same provider forgets it.
      view.rerender(
        <Wrapper>
          <Probe key="second" />
        </Wrapper>,
      );
      await settle(hook!.mutateAsync({ detail: { name: 'a' } }));

      const [first, afterReset, afterRemount] = sentKeys(fetchMock);
      expect(afterReset).not.toBe(first);
      expect(afterRemount).not.toBe(afterReset);
    });
  });
});
