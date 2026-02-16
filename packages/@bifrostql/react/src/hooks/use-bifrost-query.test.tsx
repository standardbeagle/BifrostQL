import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostQuery } from './use-bifrost-query';

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

describe('useBifrostQuery', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('queries a table and extracts data by table name', async () => {
    const mockUsers = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];
    globalThis.fetch = createFetchMock({ data: { users: mockUsers } });

    const { result } = renderHook(
      () =>
        useBifrostQuery<Array<{ id: number; name: string }>>('users', {
          fields: ['id', 'name'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(mockUsers);
  });

  it('builds query with filter and sends it', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () =>
        useBifrostQuery('users', {
          filter: { status: 'active' },
          fields: ['id'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('filter:');
    expect(body.query).toContain('status');
    expect(body.query).toContain('_eq');
  });

  it('builds query with sort options', async () => {
    globalThis.fetch = createFetchMock({ data: { orders: [] } });

    const { result } = renderHook(
      () =>
        useBifrostQuery('orders', {
          sort: [{ field: 'created_at', direction: 'desc' }],
          fields: ['id', 'total'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('sort:');
    expect(body.query).toContain('created_at desc');
  });

  it('builds query with pagination', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () =>
        useBifrostQuery('users', {
          pagination: { limit: 10, offset: 20 },
          fields: ['id'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('limit: 10');
    expect(body.query).toContain('offset: 20');
  });

  it('uses __typename when no fields specified', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(() => useBifrostQuery('users'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('__typename');
  });

  it('returns undefined data when table key is missing from response', async () => {
    globalThis.fetch = createFetchMock({ data: { other_table: [] } });

    const { result } = renderHook(
      () => useBifrostQuery('users', { fields: ['id'] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toBeUndefined();
  });

  it('respects enabled option', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () => useBifrostQuery('users', { enabled: false, fields: ['id'] }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('throws on HTTP error response', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const { result } = renderHook(
      () => useBifrostQuery('users', { fields: ['id'], retry: false }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toContain('500');
  });

  it('throws on GraphQL errors in response', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [{ message: 'Table not found' }],
    });

    const { result } = renderHook(
      () => useBifrostQuery('users', { fields: ['id'], retry: false }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Table not found');
  });

  it('combines filter, sort, and pagination in a single query', async () => {
    globalThis.fetch = createFetchMock({ data: { products: [] } });

    const { result } = renderHook(
      () =>
        useBifrostQuery('products', {
          filter: { active: true },
          sort: [{ field: 'price', direction: 'asc' }],
          pagination: { limit: 5 },
          fields: ['id', 'name', 'price'],
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toContain('filter:');
    expect(body.query).toContain('sort:');
    expect(body.query).toContain('limit: 5');
    expect(body.query).toContain('id');
    expect(body.query).toContain('name');
    expect(body.query).toContain('price');
  });

  it('provides invalidate function from underlying hook', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () => useBifrostQuery('users', { fields: ['id'] }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(typeof result.current.invalidate).toBe('function');
  });
});
