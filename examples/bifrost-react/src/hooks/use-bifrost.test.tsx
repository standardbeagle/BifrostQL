import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { useBifrost } from './use-bifrost';

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

describe('useBifrost', () => {
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
      renderHook(() => useBifrost('{ users { id } }'), {
        wrapper: Wrapper,
      });
    }).toThrow('useBifrost must be used within a BifrostProvider');
  });

  it('executes a basic query and returns data', async () => {
    const mockData = { users: [{ id: 1, name: 'Alice' }] };
    globalThis.fetch = createFetchMock({ data: mockData });

    const { result } = renderHook(() => useBifrost('{ users { id name } }'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(mockData);
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it('sends query and variables in request body', async () => {
    globalThis.fetch = createFetchMock({ data: { user: { id: 1 } } });

    const query = 'query GetUser($id: Int!) { user(id: $id) { id } }';
    const variables = { id: 42 };

    const { result } = renderHook(() => useBifrost(query, variables), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body.query).toBe(query);
    expect(body.variables).toEqual({ id: 42 });
  });

  it('omits variables key when no variables provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(() => useBifrost('{ users { id } }'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body).not.toHaveProperty('variables');
  });

  it('omits variables key when variables object is empty', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(() => useBifrost('{ users { id } }', {}), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [, fetchOptions] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(fetchOptions.body);
    expect(body).not.toHaveProperty('variables');
  });

  it('sends configured headers with request', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const wrapper = createWrapper('http://localhost/graphql', {
      Authorization: 'Bearer test-token',
    });

    const { result } = renderHook(() => useBifrost('{ users { id } }'), {
      wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

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
      () => useBifrost('{ users { id } }', undefined, { retry: false }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toContain('500');
  });

  it('throws on GraphQL errors in response', async () => {
    globalThis.fetch = createFetchMock({
      data: null,
      errors: [
        { message: 'Table not found' },
        { message: 'Permission denied' },
      ],
    });

    const { result } = renderHook(
      () => useBifrost('{ missing { id } }', undefined, { retry: false }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe(
      'Table not found, Permission denied',
    );
  });

  it('throws when response contains no data', async () => {
    globalThis.fetch = createFetchMock({});

    const { result } = renderHook(
      () => useBifrost('{ users { id } }', undefined, { retry: false }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe(
      'BifrostQL response contained no data',
    );
  });

  it('respects enabled option', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    const { result } = renderHook(
      () => useBifrost('{ users { id } }', undefined, { enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('deduplicates identical concurrent requests', async () => {
    const mockFetch = createFetchMock({ data: { users: [{ id: 1 }] } });
    globalThis.fetch = mockFetch;

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider config={{ endpoint: 'http://localhost/graphql' }}>
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    }

    const { result: result1 } = renderHook(
      () => useBifrost('{ users { id } }'),
      { wrapper: Wrapper },
    );
    const { result: result2 } = renderHook(
      () => useBifrost('{ users { id } }'),
      { wrapper: Wrapper },
    );

    await waitFor(() => {
      expect(result1.current.isSuccess).toBe(true);
      expect(result2.current.isSuccess).toBe(true);
    });

    expect(mockFetch).toHaveBeenCalledTimes(1);
  });

  it('provides invalidate function that triggers refetch', async () => {
    let callCount = 0;
    globalThis.fetch = vi.fn().mockImplementation(() => {
      callCount++;
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () =>
          Promise.resolve({
            data: { users: [{ id: callCount }] },
          }),
      });
    });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider config={{ endpoint: 'http://localhost/graphql' }}>
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    }

    const { result } = renderHook(
      () => useBifrost<{ users: Array<{ id: number }> }>('{ users { id } }'),
      { wrapper: Wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.users[0].id).toBe(1);

    await act(async () => {
      await result.current.invalidate();
    });

    await waitFor(() => expect(callCount).toBeGreaterThanOrEqual(2));
  });

  it('retries on failure with exponential backoff', async () => {
    let attempt = 0;
    globalThis.fetch = vi.fn().mockImplementation(() => {
      attempt++;
      if (attempt < 3) {
        return Promise.resolve({
          ok: false,
          status: 500,
          statusText: 'Internal Server Error',
          json: () => Promise.resolve({}),
        });
      }
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({ data: { users: [] } }),
      });
    });

    const queryClient = new QueryClient({
      defaultOptions: { queries: { gcTime: 0 } },
    });

    function Wrapper({ children }: { children: ReactNode }) {
      return (
        <QueryClientProvider client={queryClient}>
          <BifrostProvider config={{ endpoint: 'http://localhost/graphql' }}>
            {children}
          </BifrostProvider>
        </QueryClientProvider>
      );
    }

    const { result } = renderHook(
      () =>
        useBifrost('{ users { id } }', undefined, {
          retry: 3,
          retryDelay: 10,
        }),
      { wrapper: Wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true), {
      timeout: 5000,
    });

    expect(attempt).toBe(3);
  });

  it('uses correct endpoint from config', async () => {
    const customEndpoint = 'https://api.example.com/graphql';
    globalThis.fetch = createFetchMock({ data: { items: [] } });

    const { result } = renderHook(() => useBifrost('{ items { id } }'), {
      wrapper: createWrapper(customEndpoint),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const [url] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe(customEndpoint);
  });
});
