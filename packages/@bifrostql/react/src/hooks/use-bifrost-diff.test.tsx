import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostDiff } from './use-bifrost-diff';

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

describe('useBifrostDiff', () => {
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
      renderHook(() => useBifrostDiff({ table: 'users', idField: 'id' }), {
        wrapper: Wrapper,
      });
    }).toThrow('useBifrostDiff must be used within a BifrostProvider');
  });

  it('sends only changed fields to the server', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 123,
        original: { name: 'John', email: 'old@example.com', age: 30 },
        updated: { name: 'John', email: 'new@example.com', age: 30 },
      });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.variables.detail).toEqual({
      id: 123,
      email: 'new@example.com',
    });
  });

  it('does not mutate when there are no changes', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 123,
        original: { name: 'John', age: 30 },
        updated: { name: 'John', age: 30 },
      });
    });

    expect(globalThis.fetch).not.toHaveBeenCalled();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBeNull();
  });

  it('uses shallow diff strategy when configured', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () =>
        useBifrostDiff({
          table: 'users',
          idField: 'id',
          strategy: 'shallow',
        }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { name: 'John', profile: { city: 'NYC' } },
        updated: { name: 'John', profile: { city: 'NYC' } },
      });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.variables.detail).toEqual({
      id: 1,
      profile: { city: 'NYC' },
    });
  });

  it('uses deep diff strategy by default', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { name: 'John', profile: { city: 'NYC' } },
        updated: { name: 'John', profile: { city: 'NYC' } },
      });
    });

    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('detects array changes', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { tags: ['a', 'b'] },
        updated: { tags: ['a', 'b', 'c'] },
      });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.variables.detail).toEqual({
      id: 1,
      tags: ['a', 'b', 'c'],
    });
  });

  it('detects nested object changes', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { profile: { address: { city: 'NYC' } } },
        updated: { profile: { address: { city: 'LA' } } },
      });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.variables.detail).toEqual({
      id: 1,
      profile: { address: { city: 'LA' } },
    });
  });

  it('provides preview of changes without mutating', () => {
    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    const preview = result.current.preview({
      id: 1,
      original: { name: 'John', age: 30 },
      updated: { name: 'Jane', age: 30 },
    });

    expect(preview.diff.hasChanges).toBe(true);
    expect(preview.diff.changed).toEqual({ name: 'Jane' });
    expect(preview.conflicts).toEqual([]);
  });

  it('detects conflicts when lastKnown state is set', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id' }),
      { wrapper: createWrapper() },
    );

    act(() => {
      result.current.setLastKnown({ name: 'Jane', age: 30 });
    });

    await act(async () => {
      try {
        await result.current.mutateAsync({
          id: 1,
          original: { name: 'John', age: 30 },
          updated: { name: 'Jack', age: 30 },
        });
      } catch {
        // expected conflict error
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Conflict detected');
    expect(result.current.error?.message).toContain('name');
  });

  it('calls onSuccess callback after mutation', async () => {
    const mockData = { users: 1 };
    globalThis.fetch = createFetchMock({ data: mockData });

    const onSuccess = vi.fn();
    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id', onSuccess }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { name: 'John' },
        updated: { name: 'Jane' },
      });
    });

    expect(onSuccess).toHaveBeenCalledWith(mockData);
  });

  it('calls onError callback on failure', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const onError = vi.fn();
    const { result } = renderHook(
      () => useBifrostDiff({ table: 'users', idField: 'id', onError }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      try {
        await result.current.mutateAsync({
          id: 1,
          original: { name: 'John' },
          updated: { name: 'Jane' },
        });
      } catch {
        // expected
      }
    });

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0].message).toContain('500');
  });

  it('builds correct update mutation for the configured table', async () => {
    globalThis.fetch = createFetchMock({ data: { orders: 1 } });

    const { result } = renderHook(
      () => useBifrostDiff({ table: 'orders', idField: 'order_id' }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 42,
        original: { status: 'pending' },
        updated: { status: 'shipped' },
      });
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('Update_orders');
    expect(body.query).toContain('orders(update: $detail)');
    expect(body.variables.detail).toEqual({
      order_id: 42,
      status: 'shipped',
    });
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

    const { result } = renderHook(
      () =>
        useBifrostDiff({
          table: 'users',
          idField: 'id',
          invalidateQueries: ['{ users { id name } }'],
        }),
      { wrapper: Wrapper },
    );

    await act(async () => {
      await result.current.mutateAsync({
        id: 1,
        original: { name: 'John' },
        updated: { name: 'Jane' },
      });
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', '{ users { id name } }'],
    });
  });
});
