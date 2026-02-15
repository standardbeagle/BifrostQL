// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { fetchBifrostQuery } from './fetch-bifrost-query';

function createFetchMock(response: unknown, ok = true, status = 200) {
  return vi.fn().mockResolvedValue({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: () => Promise.resolve(response),
  });
}

describe('fetchBifrostQuery', () => {
  let originalFetch: typeof globalThis.fetch;
  let queryClient: QueryClient;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    queryClient.clear();
    vi.restoreAllMocks();
  });

  it('prefetches data into the query client', async () => {
    const mockData = { users: [{ id: 1, name: 'Alice' }] };
    globalThis.fetch = createFetchMock({ data: mockData });

    const result = await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
      fields: ['id', 'name'],
    });

    expect(result).toEqual([{ id: 1, name: 'Alice' }]);
  });

  it('sends request to the specified endpoint', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost:5000/graphql',
      table: 'users',
    });

    const [url] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(url).toBe('http://localhost:5000/graphql');
  });

  it('passes custom headers', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
      headers: { Authorization: 'Bearer token' },
    });

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(options.headers.Authorization).toBe('Bearer token');
  });

  it('builds query with filter options', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
      filter: { status: 'active' },
      fields: ['id'],
    });

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body.query).toContain('filter:');
    expect(body.query).toContain('status');
  });

  it('builds query with sort and pagination', async () => {
    globalThis.fetch = createFetchMock({ data: { orders: [] } });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'orders',
      sort: [{ field: 'created_at', direction: 'desc' }],
      pagination: { limit: 10, offset: 0 },
      fields: ['id'],
    });

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    const body = JSON.parse(options.body);
    expect(body.query).toContain('sort:');
    expect(body.query).toContain('limit: 10');
  });

  it('stores data under the correct query key for hydration', async () => {
    globalThis.fetch = createFetchMock({
      data: { users: [{ id: 1 }] },
    });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
      fields: ['id'],
    });

    const allQueries = queryClient.getQueryCache().getAll();
    expect(allQueries).toHaveLength(1);
    expect(allQueries[0].queryKey[0]).toBe('bifrost');
  });

  it('returns undefined for table key when fetch fails silently', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const result = await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
    });

    expect(result).toBeUndefined();
  });

  it('uses default empty headers when none provided', async () => {
    globalThis.fetch = createFetchMock({ data: { users: [] } });

    await fetchBifrostQuery(queryClient, {
      endpoint: 'http://localhost/graphql',
      table: 'users',
    });

    const [, options] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(options.headers['Content-Type']).toBe('application/json');
  });
});
