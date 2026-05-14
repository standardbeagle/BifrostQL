import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { useAppMetadata } from './use-app-metadata';
import type { AppMetadata } from './types';

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
  getToken?: () => string | null | Promise<string | null>,
) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint, headers, getToken }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': {
      label: 'Users',
      icon: 'person',
      displayFields: ['name'],
      navPlacement: 'admin',
      fields: {
        name: { widget: 'text', visible: true },
        secret: { widget: 'text', visible: false, readOnly: true },
      },
      grid: {
        defaultColumns: ['id', 'name'],
        defaultSort: ['created_at desc'],
        savedViews: {
          active: { name: 'Active only', filters: ['status = active'] },
        },
        bulkActions: ['delete'],
      },
      relationships: {
        orders: {
          targetEntity: 'sales.orders',
          kind: 'childCollection',
          foreignKeyField: 'user_id',
          displayColumns: ['total'],
          label: 'Orders',
        },
      },
    },
  },
};

describe('useAppMetadata', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('fetches /_app-metadata and returns typed entities/fields/grids/relationships', async () => {
    globalThis.fetch = createFetchMock(sampleMetadata);

    const { result } = renderHook(() => useAppMetadata(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.isError).toBe(false);
    expect(result.current.data).toEqual(sampleMetadata);

    const users = result.current.entities['dbo.users'];
    expect(users.label).toBe('Users');
    expect(users.fields?.secret.visible).toBe(false);
    expect(users.grid?.defaultColumns).toEqual(['id', 'name']);
    expect(users.grid?.savedViews?.active.name).toBe('Active only');
    expect(users.relationships?.orders.kind).toBe('childCollection');
    expect(users.relationships?.orders.targetEntity).toBe('sales.orders');
  });

  it('requests the /_app-metadata sibling path of the GraphQL endpoint', async () => {
    globalThis.fetch = createFetchMock(sampleMetadata);

    const { result } = renderHook(() => useAppMetadata(), {
      wrapper: createWrapper('http://localhost:5000/graphql'),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    const [requestUrl] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(requestUrl).toBe('http://localhost:5000/_app-metadata');
  });

  it('exposes an empty entities map before data resolves', async () => {
    globalThis.fetch = createFetchMock(sampleMetadata);

    const { result } = renderHook(() => useAppMetadata(), {
      wrapper: createWrapper(),
    });

    expect(result.current.isLoading).toBe(true);
    expect(result.current.entities).toEqual({});

    await waitFor(() => expect(result.current.isLoading).toBe(false));
  });

  it('sends a bearer token from getToken when provided', async () => {
    globalThis.fetch = createFetchMock(sampleMetadata);

    const { result } = renderHook(() => useAppMetadata(), {
      wrapper: createWrapper(
        'http://localhost:5000/graphql',
        undefined,
        () => 'tok-123',
      ),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    const [, requestInit] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock
      .calls[0];
    expect(requestInit.headers.Authorization).toBe('Bearer tok-123');
  });

  it('surfaces an error on HTTP failure', async () => {
    globalThis.fetch = createFetchMock({}, false, 500);

    const { result } = renderHook(() => useAppMetadata(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toContain('500');
    expect(result.current.data).toBeUndefined();
  });

  it('throws when used outside a BifrostProvider', () => {
    const queryClient = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );

    expect(() => renderHook(() => useAppMetadata(), { wrapper })).toThrow(
      'useAppMetadata must be used within a BifrostProvider',
    );
  });
});
