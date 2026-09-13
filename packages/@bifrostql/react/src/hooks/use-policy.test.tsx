import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  renderHook,
  waitFor,
  act,
  render,
  screen,
} from '@testing-library/react';
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

/**
 * `grants` is read on every call, so a test can change what the server would
 * answer for the next identity or the next refresh.
 */
function createPolicyFetchMock(
  grants: string[] | (() => string[]),
  tables: unknown[],
) {
  return vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
    const body = JSON.parse(String(init?.body));
    const data: Record<string, unknown> = {
      _grants: typeof grants === 'function' ? grants() : grants,
    };
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
    expect(result.current.hasColumn('ssn')).toBe(true);
    expect(result.current.hasColumn('missing')).toBe(false);
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

  it('issues one fetch for two consumers of the same table', async () => {
    // Arrange
    globalThis.fetch = createPolicyFetchMock(['admin'], [USERS_TABLE]);
    const Wrapper = createWrapper();
    function Consumer({ label }: { label: string }) {
      const policy = usePolicy('users', { identity: 'user-1' });
      return (
        <span data-testid={label}>
          {policy.isLoading ? 'loading' : String(policy.can('update'))}
        </span>
      );
    }

    // Act
    render(
      <Wrapper>
        <Consumer label="a" />
        <Consumer label="b" />
      </Wrapper>,
    );

    // Assert: both read the same cached answer from a single request.
    await waitFor(() =>
      expect(screen.getByTestId('a')).toHaveTextContent('true'),
    );
    expect(screen.getByTestId('b')).toHaveTextContent('true');
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it('refetches when the identity changes, so a new user never reads the old grants', async () => {
    // Arrange: the server answers for whoever is signed in right now.
    let serverGrants: string[] = [];
    globalThis.fetch = createPolicyFetchMock(() => serverGrants, []);

    // Act: signed out, then signed in as user-1, then switched to user-2.
    const { result, rerender } = renderHook(
      ({ identity }: { identity?: string }) =>
        usePolicy(undefined, { identity }),
      { wrapper: createWrapper(), initialProps: {} },
    );
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.grants).toEqual([]);

    serverGrants = ['dbo.users.read'];
    rerender({ identity: 'user-1' });
    await waitFor(() =>
      expect(result.current.grants).toEqual(['dbo.users.read']),
    );

    serverGrants = ['dbo.orders.read'];
    rerender({ identity: 'user-2' });

    // Assert: each identity got its own request and its own answer.
    await waitFor(() =>
      expect(result.current.grants).toEqual(['dbo.orders.read']),
    );
    expect(globalThis.fetch).toHaveBeenCalledTimes(3);
  });

  it('refetches on refresh()', async () => {
    // Arrange
    let serverGrants = ['dbo.users.read'];
    globalThis.fetch = createPolicyFetchMock(() => serverGrants, []);
    const { result } = renderHook(
      () => usePolicy(undefined, { identity: 'user-1' }),
      {
        wrapper: createWrapper(),
      },
    );
    await waitFor(() =>
      expect(result.current.grants).toEqual(['dbo.users.read']),
    );

    // Act
    serverGrants = [];
    act(() => result.current.refresh());

    // Assert
    await waitFor(() => expect(result.current.grants).toEqual([]));
    expect(globalThis.fetch).toHaveBeenCalledTimes(2);
  });

  it('surfaces a failed fetch as error and denies everything without throwing', async () => {
    // Arrange
    globalThis.fetch = vi.fn(() =>
      Promise.resolve({
        ok: false,
        status: 500,
        statusText: 'Internal Server Error',
        json: () => Promise.resolve({}),
      } as Response),
    );

    // Act
    const { result } = renderHook(
      () => usePolicy('users', { identity: 'user-1' }),
      {
        wrapper: createWrapper(),
      },
    );
    await waitFor(() => expect(result.current.isError).toBe(true));

    // Assert: fail closed, and the error is data on the result, not a throw.
    expect(result.current.error).toBeInstanceOf(Error);
    expect(result.current.isLoading).toBe(false);
    expect(result.current.grants).toEqual([]);
    expect(result.current.can('read')).toBe(false);
    expect(result.current.readable('email')).toBe(false);
    expect(result.current.writable('email')).toBe(false);
  });
});
