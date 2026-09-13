import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '../components/bifrost-provider';
import { usePolicy } from './use-policy';

/**
 * Mirrors the server wire shape: `_grants: [String!]!` and
 * `_dbSchema(graphQlName: String): [dbTableSchema!]!` whose columns carry
 * `graphQlName`, `readable` and `writable`.
 */
const USERS_TABLE = {
  graphQlName: 'users',
  allowedActions: ['read', 'update'],
  columns: [
    { graphQlName: 'id', readable: true, writable: false },
    { graphQlName: 'email', readable: true, writable: true },
    { graphQlName: 'ssn', readable: false, writable: false },
  ],
};

function createPolicyFetchMock(grants: string[], tables: unknown[]) {
  return vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
    const body = JSON.parse(String(init?.body));
    const data: Record<string, unknown> = { _grants: grants };
    if (body.query.includes('_dbSchema')) {
      data._dbSchema = tables;
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data }),
    } as Response);
  });
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint: 'http://localhost:5000/graphql' }}>
          {children}
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

function lastRequestBody() {
  const [, init] = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
  return JSON.parse(init.body);
}

describe('usePolicy', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('loads the caller grants without a table and reports loading first', async () => {
    // Arrange
    globalThis.fetch = createPolicyFetchMock(['dbo.users.read'], []);

    // Act
    const { result } = renderHook(() => usePolicy(), {
      wrapper: createWrapper(),
    });

    // Assert: the grants query runs even when no table is named.
    expect(result.current.isLoading).toBe(true);
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.grants).toEqual(['dbo.users.read']);
    expect(lastRequestBody().query).not.toContain('_dbSchema');
    expect(result.current.can('read')).toBe(false);
    expect(result.current.writable('email')).toBe(false);
  });

  it('reads allowedActions and per-column flags from the named table projection', async () => {
    // Arrange
    globalThis.fetch = createPolicyFetchMock(['admin'], [USERS_TABLE]);

    // Act
    const { result } = renderHook(() => usePolicy('users'), {
      wrapper: createWrapper(),
    });
    await waitFor(() => expect(result.current.isLoading).toBe(false));

    // Assert: the request selects the wire fields, and the helpers read them.
    const body = lastRequestBody();
    expect(body.variables).toEqual({ table: 'users' });
    expect(body.query).toContain('_dbSchema(graphQlName: $table)');
    expect(body.query).toContain('graphQlName readable writable');
    expect(body.query).not.toContain('_grants {');
    expect(result.current.grants).toEqual(['admin']);
    expect(result.current.can('update')).toBe(true);
    expect(result.current.can('delete')).toBe(false);
    expect(result.current.readable('email')).toBe(true);
    expect(result.current.readable('ssn')).toBe(false);
    expect(result.current.writable('email')).toBe(true);
    expect(result.current.writable('id')).toBe(false);
    expect(result.current.writable('missing')).toBe(false);
  });

  it('denies everything for a table the server does not project', async () => {
    // Arrange: a table the caller may not read is absent from `_dbSchema`.
    globalThis.fetch = createPolicyFetchMock([], []);

    // Act
    const { result } = renderHook(() => usePolicy('secrets'), {
      wrapper: createWrapper(),
    });
    await waitFor(() => expect(result.current.isLoading).toBe(false));

    // Assert
    expect(result.current.can('read')).toBe(false);
    expect(result.current.readable('id')).toBe(false);
    expect(result.current.writable('id')).toBe(false);
  });
});
