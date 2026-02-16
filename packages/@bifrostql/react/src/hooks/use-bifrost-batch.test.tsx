import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostBatch, BatchError } from './use-bifrost-batch';
import type { BatchOperation } from './use-bifrost-batch';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

function createSequentialFetchMock(
  responses: Array<{ response: unknown; ok?: boolean; status?: number }>,
) {
  let callIndex = 0;
  return vi.fn().mockImplementation(() => {
    const entry = responses[callIndex] ?? responses[responses.length - 1];
    callIndex++;
    const ok = entry.ok ?? true;
    const status = entry.status ?? 200;
    return Promise.resolve({
      ok,
      status,
      statusText: ok ? 'OK' : 'Internal Server Error',
      json: () => Promise.resolve(entry.response),
    });
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

describe('useBifrostBatch', () => {
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
      renderHook(() => useBifrostBatch(), { wrapper: Wrapper });
    }).toThrow('useBifrostBatch must be used within a BifrostProvider');
  });

  it('executes all operations successfully', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const operations: BatchOperation[] = [
      { type: 'insert', table: 'users', data: { name: 'Alice' } },
      { type: 'update', table: 'users', id: 123, data: { status: 'active' } },
    ];

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.results).toHaveLength(2);
    expect(result.current.data?.errors).toHaveLength(0);
    expect(globalThis.fetch).toHaveBeenCalledTimes(2);
  });

  it('sends correct mutation for each operation type', async () => {
    globalThis.fetch = createFetchMock({ data: { result: 1 } });

    const operations: BatchOperation[] = [
      { type: 'insert', table: 'users', data: { name: 'Alice' } },
      { type: 'update', table: 'users', id: 5, data: { name: 'Bob' } },
      {
        type: 'upsert',
        table: 'settings',
        key: { user_id: 1 },
        data: { theme: 'dark' },
      },
      { type: 'delete', table: 'orders', id: 456 },
    ];

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    const calls = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls;
    expect(calls).toHaveLength(4);

    const bodies = calls.map((call: unknown[]) =>
      JSON.parse((call[1] as RequestInit).body as string),
    );

    // Sorted by dependency: insert, upsert, update, delete
    expect(bodies[0].query).toContain('Insert');
    expect(bodies[0].query).toContain('users(insert: $detail)');
    expect(bodies[0].variables.detail).toEqual({ name: 'Alice' });

    expect(bodies[1].query).toContain('Upsert');
    expect(bodies[1].query).toContain('settings(upsert: $detail)');
    expect(bodies[1].variables.detail).toEqual({ user_id: 1, theme: 'dark' });

    expect(bodies[2].query).toContain('Update');
    expect(bodies[2].query).toContain('users(update: $detail)');
    expect(bodies[2].variables.detail).toEqual({ id: 5, name: 'Bob' });

    expect(bodies[3].query).toContain('Delete');
    expect(bodies[3].query).toContain('orders(delete: $detail)');
    expect(bodies[3].variables.detail).toEqual({ id: 456 });
  });

  it('throws BatchError on failure without partial success', async () => {
    globalThis.fetch = createSequentialFetchMock([
      { response: { data: { users: 1 } } },
      { response: {}, ok: false, status: 500 },
    ]);

    const operations: BatchOperation[] = [
      { type: 'insert', table: 'users', data: { name: 'Alice' } },
      { type: 'insert', table: 'users', data: { name: 'Bob' } },
    ];

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync(operations);
      } catch {
        // expected
      }
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(BatchError);

    const batchError = result.current.error as BatchError;
    expect(batchError.results).toHaveLength(1);
    expect(batchError.errors).toHaveLength(1);
    expect(batchError.message).toContain('Batch operation failed');
  });

  it('collects all errors in partial success mode', async () => {
    globalThis.fetch = createSequentialFetchMock([
      { response: { data: { users: 1 } } },
      { response: {}, ok: false, status: 500 },
      { response: { data: { users: 1 } } },
    ]);

    const operations: BatchOperation[] = [
      { type: 'insert', table: 'users', data: { name: 'Alice' } },
      { type: 'insert', table: 'users', data: { name: 'Bad' } },
      { type: 'insert', table: 'users', data: { name: 'Charlie' } },
    ];

    const { result } = renderHook(
      () => useBifrostBatch({ allowPartialSuccess: true }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.results).toHaveLength(2);
    expect(result.current.data?.errors).toHaveLength(1);
    expect(result.current.data?.errors[0].index).toBe(1);
  });

  it('tracks progress through onProgress callback', async () => {
    globalThis.fetch = createFetchMock({ data: { result: 1 } });

    const progressUpdates: Array<{
      total: number;
      completed: number;
      failed: number;
      current: number;
    }> = [];
    const onProgress = vi.fn((p) => progressUpdates.push({ ...p }));

    const operations: BatchOperation[] = [
      { type: 'insert', table: 'users', data: { name: 'Alice' } },
      { type: 'insert', table: 'users', data: { name: 'Bob' } },
    ];

    const { result } = renderHook(() => useBifrostBatch({ onProgress }), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    expect(onProgress).toHaveBeenCalled();

    // Initial progress: total=2, completed=0, current=0
    expect(progressUpdates[0]).toEqual({
      total: 2,
      completed: 0,
      failed: 0,
      current: 0,
    });

    // After all complete, last update should show completed=2
    const lastUpdate = progressUpdates[progressUpdates.length - 1];
    expect(lastUpdate.completed).toBe(2);
    expect(lastUpdate.failed).toBe(0);
  });

  it('sorts operations by dependency order', async () => {
    globalThis.fetch = createFetchMock({ data: { result: 1 } });

    const operations: BatchOperation[] = [
      { type: 'delete', table: 'orders', id: 1 },
      { type: 'update', table: 'users', id: 2, data: { name: 'Updated' } },
      { type: 'insert', table: 'users', data: { name: 'New' } },
      {
        type: 'upsert',
        table: 'settings',
        key: { user_id: 1 },
        data: { theme: 'dark' },
      },
    ];

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    const calls = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls;
    const bodies = calls.map((call: unknown[]) =>
      JSON.parse((call[1] as RequestInit).body as string),
    );

    // Order: insert, upsert, update, delete
    expect(bodies[0].query).toContain('Insert');
    expect(bodies[1].query).toContain('Upsert');
    expect(bodies[2].query).toContain('Update');
    expect(bodies[3].query).toContain('Delete');
  });

  it('preserves original indices in results after reordering', async () => {
    globalThis.fetch = createFetchMock({ data: { result: 1 } });

    const operations: BatchOperation[] = [
      { type: 'delete', table: 'orders', id: 1 },
      { type: 'insert', table: 'users', data: { name: 'New' } },
    ];

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync(operations);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Insert (index 1) runs first, delete (index 0) runs second
    expect(result.current.data!.results[0].index).toBe(1);
    expect(result.current.data!.results[1].index).toBe(0);
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
        useBifrostBatch({
          invalidateQueries: ['{ users { id } }', '{ orders { id } }'],
        }),
      { wrapper: Wrapper },
    );

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', '{ users { id } }'],
    });
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['bifrost', '{ orders { id } }'],
    });
  });

  it('calls onSuccess callback with batch result', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });
    const onSuccess = vi.fn();

    const { result } = renderHook(() => useBifrostBatch({ onSuccess }), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);
    });

    expect(onSuccess).toHaveBeenCalledTimes(1);
    expect(onSuccess.mock.calls[0][0].results).toHaveLength(1);
    expect(onSuccess.mock.calls[0][0].errors).toHaveLength(0);
  });

  it('calls onError callback on failure', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);
    const onError = vi.fn();

    const { result } = renderHook(() => useBifrostBatch({ onError }), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      try {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
        ]);
      } catch {
        // expected
      }
    });

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(BatchError);
  });

  it('provides progress via getProgress', async () => {
    globalThis.fetch = createFetchMock({ data: { result: 1 } });

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    const initialProgress = result.current.getProgress();
    expect(initialProgress).toEqual({
      total: 0,
      completed: 0,
      failed: 0,
      current: 0,
    });

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);
    });

    const finalProgress = result.current.getProgress();
    expect(finalProgress.total).toBe(1);
    expect(finalProgress.completed).toBe(1);
  });

  it('sends configured headers with requests', async () => {
    globalThis.fetch = createFetchMock({ data: { users: 1 } });

    const wrapper = createWrapper('http://localhost/graphql', {
      Authorization: 'Bearer test-token',
    });

    const { result } = renderHook(() => useBifrostBatch(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);
    });

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(fetchOptions.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer test-token',
    });
  });

  it('handles empty operations array', async () => {
    globalThis.fetch = createFetchMock({ data: {} });

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync([]);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.results).toHaveLength(0);
    expect(result.current.data?.errors).toHaveLength(0);
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('aggregates errors from multiple failed operations', async () => {
    globalThis.fetch = createSequentialFetchMock([
      { response: {}, ok: false, status: 400 },
      { response: {}, ok: false, status: 500 },
      { response: { data: { users: 1 } } },
    ]);

    const { result } = renderHook(
      () => useBifrostBatch({ allowPartialSuccess: true }),
      { wrapper: createWrapper() },
    );

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Bad1' } },
        { type: 'insert', table: 'users', data: { name: 'Bad2' } },
        { type: 'insert', table: 'users', data: { name: 'Good' } },
      ]);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.errors).toHaveLength(2);
    expect(result.current.data?.errors[0].error.message).toContain('400');
    expect(result.current.data?.errors[1].error.message).toContain('500');
    expect(result.current.data?.results).toHaveLength(1);
  });
});
