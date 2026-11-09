import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostBatch, BatchError } from './use-bifrost-batch';
import type { BatchOperation, BatchProgress } from './use-bifrost-batch';
import wireContract from './__fixtures__/batch-wire-contract.json';

function jsonResponse(response: unknown, ok = true, status = 200) {
  return {
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  };
}

/**
 * The network is the only thing mocked. Replies follow the server's real
 * `<table>_batch` shape, an `Int` of affected rows (see batch-wire-contract.json,
 * which ReactBatchWireContractTests runs against a real server).
 */
function mockFetchReplies(...replies: unknown[]) {
  let callIndex = 0;
  const fetchMock = vi.fn().mockImplementation(() => {
    const reply = replies[callIndex] ?? replies[replies.length - 1];
    callIndex++;
    return Promise.resolve(jsonResponse(reply));
  });
  globalThis.fetch = fetchMock;
  return fetchMock;
}

function sentBodies(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls.map((call: unknown[]) =>
    JSON.parse((call[1] as RequestInit).body as string),
  );
}

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function createWrapper(
  headers?: Record<string, string>,
  queryClient = createQueryClient(),
) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider
          config={{ endpoint: 'http://localhost:5000/graphql', headers }}
        >
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

async function runToFailure(
  operations: BatchOperation[],
  options: Parameters<typeof useBifrostBatch>[0] = {},
  queryClient = createQueryClient(),
): Promise<BatchError> {
  const { result } = renderHook(() => useBifrostBatch(options), {
    wrapper: createWrapper(undefined, queryClient),
  });
  let caught: unknown;
  await act(async () => {
    try {
      await result.current.mutateAsync(operations);
    } catch (err) {
      caught = err;
    }
  });
  expect(caught).toBeInstanceOf(BatchError);
  return caught as BatchError;
}

const USERS_BATCH =
  'mutation Batch($actions: [batch_users!]!) { users_batch(actions: $actions) }';
const ORDERS_BATCH =
  'mutation Batch($actions: [batch_orders!]!) { orders_batch(actions: $actions) }';

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
    const queryClient = createQueryClient();

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

  describe('one table', () => {
    it('sends N operations as one fetch carrying <table>_batch', async () => {
      const fetchMock = mockFetchReplies({ data: { users_batch: 3 } });

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'update', table: 'users', id: 5, data: { name: 'Bob' } },
          { type: 'delete', table: 'users', id: 6 },
        ]);
      });

      expect(fetchMock).toHaveBeenCalledTimes(1);
      const [body] = sentBodies(fetchMock);
      expect(body.query).toBe(USERS_BATCH);
      expect(body.variables.actions).toEqual([
        { insert: { name: 'Alice' } },
        { update: { id: 5, name: 'Bob' } },
        { delete: { id: 6 } },
      ]);
    });

    it('sends exactly what the wire contract says the server accepts', async () => {
      const { operations, request, reply } = wireContract.mixed;
      const fetchMock = mockFetchReplies(reply);

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync(operations as BatchOperation[]);
      });

      expect(sentBodies(fetchMock)).toEqual([request]);
      expect(result.current.data?.results).toEqual([
        { table: 'items', indices: [2, 1, 0, 3], affected: 4 },
      ]);
    });

    it('sends a failing document whole, once, as the wire contract says', async () => {
      const { operations, request, reply } = wireContract.failing;
      const fetchMock = mockFetchReplies(reply);

      const error = await runToFailure(operations as BatchOperation[]);

      expect(sentBodies(fetchMock)).toEqual([request]);
      expect(error.results).toEqual([]);
      expect(error.failed.table).toBe('items');
      expect(error.failed.indices).toEqual([0, 2, 1]);
      expect(error.failed.error.message).toBe("the server's refusal");
      expect(error.notAttempted).toEqual([]);
    });

    it('does not chunk past the server default batch-max-size', async () => {
      // Chunking would quietly turn one transaction into several, so an
      // oversized batch reaches the server whole and the server refuses it.
      const fetchMock = mockFetchReplies({ data: { users_batch: 150 } });
      const operations: BatchOperation[] = Array.from(
        { length: 150 },
        (_, i) => ({
          type: 'insert',
          table: 'users',
          data: { name: `user-${i}` },
        }),
      );

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync(operations);
      });

      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(sentBodies(fetchMock)[0].variables.actions).toHaveLength(150);
    });

    it('keeps caller order for a same-table delete-then-insert replace', async () => {
      const fetchMock = mockFetchReplies({ data: { users_batch: 2 } });

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'delete', table: 'users', id: 1 },
          { type: 'insert', table: 'users', data: { id: 1, name: 'Replaced' } },
        ]);
      });

      expect(fetchMock).toHaveBeenCalledTimes(1);
      expect(sentBodies(fetchMock)[0].variables.actions).toEqual([
        { delete: { id: 1 } },
        { insert: { id: 1, name: 'Replaced' } },
      ]);
      expect(result.current.data?.results[0].indices).toEqual([0, 1]);
    });

    it('reports fewer affected rows than operations without failing', async () => {
      // A delete that matches no row commits with 0 affected on the server.
      mockFetchReplies({ data: { users_batch: 1 } });

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'delete', table: 'users', id: 404 },
        ]);
      });

      expect(result.current.data?.results).toEqual([
        { table: 'users', indices: [0, 1], affected: 1 },
      ]);
    });

    it('refuses a reply with no affected-row count', async () => {
      mockFetchReplies({ data: { users_batch: null } });

      const error = await runToFailure([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);

      expect(error.failed.error.message).toMatch(/affected-row count/);
    });
  });

  describe('several tables', () => {
    it('sends one document per table when the verbs do not interleave', async () => {
      const fetchMock = mockFetchReplies(
        { data: { users_batch: 2 } },
        { data: { orders_batch: 1 } },
      );

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'insert', table: 'orders', data: { total: 1 } },
          { type: 'insert', table: 'users', data: { name: 'Bob' } },
        ]);
      });

      expect(sentBodies(fetchMock).map((b) => b.query)).toEqual([
        USERS_BATCH,
        ORDERS_BATCH,
      ]);
      expect(result.current.data?.results).toEqual([
        { table: 'users', indices: [0, 2], affected: 2 },
        { table: 'orders', indices: [1], affected: 1 },
      ]);
    });

    it('splits a table where dependency order passes through another table', async () => {
      // The users row cannot go until the orders row that references it has
      // gone, so one document per table would fail on the foreign key.
      const fetchMock = mockFetchReplies(
        { data: { users_batch: 1 } },
        { data: { orders_batch: 2 } },
        { data: { users_batch: 1 } },
      );

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Parent' } },
          { type: 'insert', table: 'orders', data: { user_id: 1 } },
          { type: 'delete', table: 'orders', id: 9 },
          { type: 'delete', table: 'users', id: 2 },
        ]);
      });

      expect(
        sentBodies(fetchMock).map((b) => [b.query, b.variables.actions.length]),
      ).toEqual([
        [USERS_BATCH, 1],
        [ORDERS_BATCH, 2],
        [USERS_BATCH, 1],
      ]);
      expect(result.current.data?.results.map((r) => r.indices)).toEqual([
        [0],
        [1, 2],
        [3],
      ]);
    });

    it('orders documents by verb, then tables by first mention within that verb', async () => {
      const fetchMock = mockFetchReplies(
        { data: { orders_batch: 1 } },
        { data: { users_batch: 2 } },
        { data: { orders_batch: 1 } },
      );

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'update', table: 'users', id: 1, data: { name: 'Edited' } },
          { type: 'update', table: 'orders', id: 2, data: { total: 2 } },
          { type: 'insert', table: 'orders', data: { total: 1 } },
          { type: 'insert', table: 'users', data: { name: 'New' } },
        ]);
      });

      expect(result.current.data?.results.map((r) => r.indices)).toEqual([
        [2],
        [3, 0],
        [1],
      ]);
      expect(sentBodies(fetchMock).map((b) => b.query)).toEqual([
        ORDERS_BATCH,
        USERS_BATCH,
        ORDERS_BATCH,
      ]);
    });

    it('keeps caller order across tables once any table has replace intent', async () => {
      const fetchMock = mockFetchReplies(
        { data: { orders_batch: 1 } },
        { data: { users_batch: 2 } },
        { data: { orders_batch: 1 } },
      );

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'delete', table: 'orders', id: 9 },
          { type: 'delete', table: 'users', id: 1 },
          { type: 'insert', table: 'users', data: { id: 1, name: 'Replaced' } },
          { type: 'insert', table: 'orders', data: { user_id: 1 } },
        ]);
      });

      expect(sentBodies(fetchMock).map((b) => b.query)).toEqual([
        ORDERS_BATCH,
        USERS_BATCH,
        ORDERS_BATCH,
      ]);
      expect(result.current.data?.results.map((r) => r.indices)).toEqual([
        [0],
        [1, 2],
        [3],
      ]);
    });

    it('stops at the first failed document and names what committed and what never ran', async () => {
      const fetchMock = mockFetchReplies(
        { data: { users_batch: 1 } },
        { data: { orders_batch: null }, errors: [{ message: 'refused' }] },
      );

      const error = await runToFailure([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
        { type: 'insert', table: 'orders', data: { total: 1 } },
        { type: 'update', table: 'users', id: 1, data: { name: 'Later' } },
      ]);

      expect(fetchMock).toHaveBeenCalledTimes(2);
      expect(error.results).toEqual([
        { table: 'users', indices: [0], affected: 1 },
      ]);
      expect(error.failed.table).toBe('orders');
      expect(error.failed.indices).toEqual([1]);
      expect(error.notAttempted).toEqual([2]);
      expect(error.message).toContain('orders');
      expect(error.message).toContain('refused');
    });
  });

  describe('refusing an operation that cannot be sent', () => {
    it('refuses an update with no row identity and sends nothing at all', async () => {
      const fetchMock = mockFetchReplies({ data: { users_batch: 1 } });
      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await expect(
          result.current.mutateAsync([
            { type: 'insert', table: 'users', data: { name: 'Fine' } },
            { type: 'update', table: 'users', data: { name: 'Alice' } },
          ]),
        ).rejects.toThrow(/operation 1.*no row identity/i);
      });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('refuses an insert with no columns, which the server would silently drop', async () => {
      const fetchMock = mockFetchReplies({ data: { users_batch: 0 } });
      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await expect(
          result.current.mutateAsync([{ type: 'insert', table: 'users' }]),
        ).rejects.toThrow(/operation 0.*no columns/i);
      });
      expect(fetchMock).not.toHaveBeenCalled();
    });

    it('refuses an invalid table name before sending anything', async () => {
      const fetchMock = mockFetchReplies({ data: { users_batch: 1 } });
      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        await expect(
          result.current.mutateAsync([
            { type: 'insert', table: 'users', data: { name: 'Fine' } },
            { type: 'insert', table: 'bad table', data: { name: 'x' } },
          ]),
        ).rejects.toThrow(/table/i);
      });
      expect(fetchMock).not.toHaveBeenCalled();
    });
  });

  describe('invalidation', () => {
    const invalidateQueries = ['{ users { id } }'];
    const invalidated = { queryKey: ['bifrost', '{ users { id } }'] };

    it('invalidates when the batch commits', async () => {
      mockFetchReplies({ data: { users_batch: 1 } });
      const queryClient = createQueryClient();
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

      const { result } = renderHook(
        () => useBifrostBatch({ invalidateQueries }),
        { wrapper: createWrapper(undefined, queryClient) },
      );

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
        ]);
      });

      expect(invalidateSpy).toHaveBeenCalledWith(invalidated);
    });

    it('does not invalidate when the only document rolled back', async () => {
      mockFetchReplies({
        data: { users_batch: null },
        errors: [{ message: 'constraint violation' }],
      });
      const queryClient = createQueryClient();
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

      await runToFailure(
        [{ type: 'insert', table: 'users', data: { name: 'Alice' } }],
        { invalidateQueries },
        queryClient,
      );

      expect(invalidateSpy).not.toHaveBeenCalled();
    });

    it('invalidates when a later document fails after an earlier one committed', async () => {
      mockFetchReplies(
        { data: { users_batch: 1 } },
        { data: { orders_batch: null }, errors: [{ message: 'refused' }] },
      );
      const queryClient = createQueryClient();
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

      await runToFailure(
        [
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'insert', table: 'orders', data: { total: 1 } },
        ],
        { invalidateQueries },
        queryClient,
      );

      expect(invalidateSpy).toHaveBeenCalledWith(invalidated);
    });
  });

  describe('progress', () => {
    it('reports per document, counted in operations', async () => {
      mockFetchReplies(
        { data: { users_batch: 2 } },
        { data: { orders_batch: 1 } },
      );
      const updates: BatchProgress[] = [];

      const { result } = renderHook(
        () => useBifrostBatch({ onProgress: (p) => updates.push({ ...p }) }),
        { wrapper: createWrapper() },
      );

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'insert', table: 'users', data: { name: 'Bob' } },
          { type: 'insert', table: 'orders', data: { total: 1 } },
        ]);
      });

      expect(updates).toEqual([
        {
          total: 3,
          completed: 0,
          failed: 0,
          documents: 2,
          documentsSettled: 0,
        },
        {
          total: 3,
          completed: 2,
          failed: 0,
          documents: 2,
          documentsSettled: 1,
        },
        {
          total: 3,
          completed: 3,
          failed: 0,
          documents: 2,
          documentsSettled: 2,
        },
      ]);
    });

    it('counts every operation of a failed document as failed', async () => {
      mockFetchReplies({
        data: { users_batch: null },
        errors: [{ message: 'refused' }],
      });
      const updates: BatchProgress[] = [];

      await runToFailure(
        [
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
          { type: 'insert', table: 'users', data: { name: 'Bob' } },
        ],
        { onProgress: (p) => updates.push({ ...p }) },
      );

      expect(updates[updates.length - 1]).toEqual({
        total: 2,
        completed: 0,
        failed: 2,
        documents: 1,
        documentsSettled: 1,
      });
    });

    it('provides progress via getProgress', async () => {
      mockFetchReplies({ data: { users_batch: 1 } });

      const { result } = renderHook(() => useBifrostBatch(), {
        wrapper: createWrapper(),
      });

      expect(result.current.getProgress()).toEqual({
        total: 0,
        completed: 0,
        failed: 0,
        documents: 0,
        documentsSettled: 0,
      });

      await act(async () => {
        await result.current.mutateAsync([
          { type: 'insert', table: 'users', data: { name: 'Alice' } },
        ]);
      });

      expect(result.current.getProgress()).toEqual({
        total: 1,
        completed: 1,
        failed: 0,
        documents: 1,
        documentsSettled: 1,
      });
    });
  });

  it('calls onSuccess with the batch result', async () => {
    mockFetchReplies({ data: { users_batch: 1 } });
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
    expect(onSuccess.mock.calls[0][0].results).toEqual([
      { table: 'users', indices: [0], affected: 1 },
    ]);
  });

  it('calls onError with a BatchError on an HTTP failure', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({}, false, 500));
    const onError = vi.fn();

    await runToFailure(
      [{ type: 'insert', table: 'users', data: { name: 'Alice' } }],
      { onError },
    );

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(BatchError);
    expect(onError.mock.calls[0][0].failed.error.message).toContain('500');
  });

  it('sends configured headers with requests', async () => {
    const fetchMock = mockFetchReplies({ data: { users_batch: 1 } });

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper({ Authorization: 'Bearer test-token' }),
    });

    await act(async () => {
      await result.current.mutateAsync([
        { type: 'insert', table: 'users', data: { name: 'Alice' } },
      ]);
    });

    const [, fetchOptions] = fetchMock.mock.calls[0];
    expect(fetchOptions.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer test-token',
    });
  });

  it('sends nothing for an empty operations array', async () => {
    const fetchMock = mockFetchReplies({ data: {} });

    const { result } = renderHook(() => useBifrostBatch(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      await result.current.mutateAsync([]);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.results).toEqual([]);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
