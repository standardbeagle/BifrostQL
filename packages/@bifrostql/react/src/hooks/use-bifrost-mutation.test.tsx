import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostMutation } from './use-bifrost-mutation';

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
      queryKey: ['bifrost', '{ users { id } }'],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', '{ users { id name } }'],
    });
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
});
