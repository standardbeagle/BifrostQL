import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrostInfinite } from './use-bifrost-infinite';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

function createSequentialFetchMock(responses: unknown[]) {
  let callIndex = 0;
  return vi.fn().mockImplementation(() => {
    const response = responses[callIndex] ?? responses[responses.length - 1];
    callIndex++;
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve(response),
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

describe('useBifrostInfinite', () => {
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
      defaultOptions: { queries: { retry: false } },
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
          useBifrostInfinite(
            '{ users { id name } }',
            (offset: number) => ({ offset, limit: 10 }),
            {
              initialPageParam: 0,
              getNextPageParam: () => undefined,
            },
          ),
        { wrapper: Wrapper },
      );
    }).toThrow('useBifrostInfinite must be used within a BifrostProvider');
  });

  it('fetches the first page with offset-based pagination', async () => {
    const page1 = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];
    globalThis.fetch = createFetchMock({ data: { users: page1 } });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<
          { users: Array<{ id: number; name: string }> },
          number
        >(
          '{ users(offset: $offset, limit: $limit) { id name } }',
          (offset) => ({ offset, limit: 2 }),
          {
            initialPageParam: 0,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 2 ? 2 : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.pages).toHaveLength(1);
    expect(result.current.data?.pages[0].users).toEqual(page1);
    expect(result.current.hasNextPage).toBe(true);
  });

  it('fetches multiple pages with offset-based pagination', async () => {
    const page1 = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];
    const page2 = [{ id: 3, name: 'Charlie' }];

    globalThis.fetch = createSequentialFetchMock([
      { data: { users: page1 } },
      { data: { users: page2 } },
    ]);

    const { result } = renderHook(
      () =>
        useBifrostInfinite<
          { users: Array<{ id: number; name: string }> },
          number
        >(
          '{ users(offset: $offset, limit: $limit) { id name } }',
          (offset) => ({ offset, limit: 2 }),
          {
            initialPageParam: 0,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 2 ? 2 : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    await act(async () => {
      await result.current.fetchNextPage();
    });

    await waitFor(() => expect(result.current.data?.pages).toHaveLength(2));

    expect(result.current.data?.pages[0].users).toEqual(page1);
    expect(result.current.data?.pages[1].users).toEqual(page2);
    expect(result.current.hasNextPage).toBe(false);
  });

  it('fetches pages with cursor-based pagination', async () => {
    const page1 = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];
    const page2 = [{ id: 3, name: 'Charlie' }];

    globalThis.fetch = createSequentialFetchMock([
      { data: { users: page1 } },
      { data: { users: page2 } },
    ]);

    const { result } = renderHook(
      () =>
        useBifrostInfinite<
          { users: Array<{ id: number; name: string }> },
          number | undefined
        >(
          'query($cursor: Int, $limit: Int) { users(filter: { id: { _gt: $cursor } }, limit: $limit) { id name } }',
          (cursor) => ({ cursor: cursor ?? 0, limit: 2 }),
          {
            initialPageParam: undefined,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 2
                ? lastPage.users[lastPage.users.length - 1].id
                : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.pages[0].users).toEqual(page1);
    expect(result.current.hasNextPage).toBe(true);

    await act(async () => {
      await result.current.fetchNextPage();
    });

    await waitFor(() => expect(result.current.data?.pages).toHaveLength(2));

    expect(result.current.data?.pages[1].users).toEqual(page2);
    expect(result.current.hasNextPage).toBe(false);
  });

  it('supports bidirectional pagination with getPreviousPageParam', async () => {
    const page1 = [
      { id: 3, name: 'Charlie' },
      { id: 4, name: 'Diana' },
    ];
    const page0 = [
      { id: 1, name: 'Alice' },
      { id: 2, name: 'Bob' },
    ];

    globalThis.fetch = createSequentialFetchMock([
      { data: { users: page1 } },
      { data: { users: page0 } },
    ]);

    const { result } = renderHook(
      () =>
        useBifrostInfinite<
          { users: Array<{ id: number; name: string }> },
          number
        >(
          '{ users(offset: $offset, limit: $limit) { id name } }',
          (offset) => ({ offset, limit: 2 }),
          {
            initialPageParam: 2,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 2 ? undefined : undefined,
            getPreviousPageParam: (firstPage) =>
              firstPage.users[0]?.id > 1 ? 0 : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.hasPreviousPage).toBe(true);

    await act(async () => {
      await result.current.fetchPreviousPage();
    });

    await waitFor(() => expect(result.current.data?.pages).toHaveLength(2));

    expect(result.current.data?.pages[0].users).toEqual(page0);
    expect(result.current.data?.pages[1].users).toEqual(page1);
  });

  it('reports isFetchingNextPage during page fetch', async () => {
    let resolveSecondFetch: (() => void) | undefined;
    let callCount = 0;

    globalThis.fetch = vi.fn().mockImplementation(() => {
      callCount++;
      if (callCount === 1) {
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve({ data: { users: [{ id: 1 }] } }),
        });
      }
      return new Promise((resolve) => {
        resolveSecondFetch = () =>
          resolve({
            ok: true,
            status: 200,
            statusText: 'OK',
            json: () => Promise.resolve({ data: { users: [] } }),
          });
      });
    });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: Array<{ id: number }> }, number>(
          '{ users(offset: $offset, limit: $limit) { id } }',
          (offset) => ({ offset, limit: 1 }),
          {
            initialPageParam: 0,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 1 ? 1 : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    act(() => {
      result.current.fetchNextPage();
    });

    await waitFor(() => expect(result.current.isFetchingNextPage).toBe(true));

    await act(async () => {
      resolveSecondFetch?.();
    });

    await waitFor(() => expect(result.current.isFetchingNextPage).toBe(false));
  });

  it('reports hasNextPage as false when getNextPageParam returns undefined', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: Array<{ id: number }> }, number>(
          '{ users(offset: $offset, limit: $limit) { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: (lastPage) =>
              lastPage.users.length === 10 ? 10 : undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.hasNextPage).toBe(false);
  });

  it('sends correct variables built from pageParam', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [{ id: 1 }] } });

    renderHook(
      () =>
        useBifrostInfinite<{ users: Array<{ id: number }> }, number>(
          '{ users(offset: $offset, limit: $limit) { id } }',
          (offset) => ({ offset, limit: 25 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(1));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.variables).toEqual({ offset: 0, limit: 25 });
  });

  it('passes headers from config', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const wrapper = createWrapper('http://localhost/graphql', {
      Authorization: 'Bearer test-token',
    });

    renderHook(
      () =>
        useBifrostInfinite<{ users: unknown[] }, number>(
          '{ users { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
          },
        ),
      { wrapper },
    );

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(1));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(fetchOptions.headers).toEqual({
      'Content-Type': 'application/json',
      Authorization: 'Bearer test-token',
    });
  });

  it('throws on HTTP error response', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: unknown[] }, number>(
          '{ users { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
            retry: false,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toContain('500');
  });

  it('throws on GraphQL errors in response', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [{ message: 'Permission denied' }],
    });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: unknown[] }, number>(
          '{ users { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
            retry: false,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Permission denied');
  });

  it('respects enabled option', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: unknown[] }, number>(
          '{ users { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
            enabled: false,
          },
        ),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('provides invalidate function', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: unknown[] }, number>(
          '{ users { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(typeof result.current.invalidate).toBe('function');
  });

  it('uses correct endpoint from config', async () => {
    const customEndpoint = 'https://api.example.com/graphql';
    globalThis.fetch = createFetchMock({ data: { items: [] } });

    renderHook(
      () =>
        useBifrostInfinite<{ items: unknown[] }, number>(
          '{ items { id } }',
          (offset) => ({ offset, limit: 10 }),
          {
            initialPageParam: 0,
            getNextPageParam: () => undefined,
          },
        ),
      { wrapper: createWrapper(customEndpoint) },
    );

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(1));

    const [url] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe(customEndpoint);
  });

  it('merges all pages into data.pages array', async () => {
    const pages = [
      { data: { users: [{ id: 1 }, { id: 2 }] } },
      { data: { users: [{ id: 3 }, { id: 4 }] } },
      { data: { users: [{ id: 5 }] } },
    ];

    globalThis.fetch = createSequentialFetchMock(pages);

    const { result } = renderHook(
      () =>
        useBifrostInfinite<{ users: Array<{ id: number }> }, number>(
          '{ users(offset: $offset, limit: $limit) { id } }',
          (offset) => ({ offset, limit: 2 }),
          {
            initialPageParam: 0,
            getNextPageParam: (lastPage, allPages) => {
              if (lastPage.users.length < 2) return undefined;
              return allPages.reduce(
                (total, page) => total + page.users.length,
                0,
              );
            },
          },
        ),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    await act(async () => {
      await result.current.fetchNextPage();
    });
    await waitFor(() => expect(result.current.data?.pages).toHaveLength(2));

    await act(async () => {
      await result.current.fetchNextPage();
    });
    await waitFor(() => expect(result.current.data?.pages).toHaveLength(3));

    const allUsers = result.current.data!.pages.flatMap((p) => p.users);
    expect(allUsers).toEqual([
      { id: 1 },
      { id: 2 },
      { id: 3 },
      { id: 4 },
      { id: 5 },
    ]);
    expect(result.current.hasNextPage).toBe(false);
  });
});
