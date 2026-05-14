import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { SessionProvider } from './session-provider';
import { useSession } from './use-session';
import type { AppIdentity } from './session-context';

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? 'OK' : 'Error',
    json: () => Promise.resolve(body),
  } as Response;
}

function createWrapper(
  endpoint = 'http://localhost:5000/graphql',
  headers?: Record<string, string>,
  getToken?: () => string | null | Promise<string | null>,
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint, headers, getToken }}>
          <SessionProvider>{children}</SessionProvider>
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

const sampleIdentity: AppIdentity = {
  id: 'user-1',
  provider: 'local',
  email: 'user@example.com',
  displayName: 'Test User',
  tenantId: 'tenant-1',
  orgIds: ['org-1'],
  roles: ['admin'],
  permissions: ['users.read', 'users.write'],
  claims: { custom: 'value' },
};

describe('SessionProvider / useSession', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('exposes the authenticated identity, permissions, and auth state', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(sampleIdentity));

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.isAuthenticated).toBe(true);
    expect(result.current.identity).toEqual(sampleIdentity);
    expect(result.current.permissions).toEqual([
      'users.read',
      'users.write',
    ]);
    expect(result.current.error).toBeNull();
  });

  it('requests the /auth/session sibling path with credentials included', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(sampleIdentity));
    globalThis.fetch = fetchMock;

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper('http://localhost:5000/graphql'),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://localhost:5000/auth/session');
    expect(init.credentials).toBe('include');
  });

  it('treats a 401 as unauthenticated, not an error', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({}, 401));

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.identity).toBeNull();
    expect(result.current.permissions).toEqual([]);
    expect(result.current.error).toBeNull();
  });

  it('reports the loading state before the session resolves', () => {
    globalThis.fetch = vi.fn().mockReturnValue(new Promise(() => {}));

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    expect(result.current.isLoading).toBe(true);
    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.identity).toBeNull();
    expect(result.current.permissions).toEqual([]);
  });

  it('degrades to unauthenticated and surfaces the error on a non-401 failure', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({}, 500));

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.identity).toBeNull();
    expect(result.current.error?.message).toContain('500');
  });

  it('degrades to unauthenticated when the endpoint is unreachable', async () => {
    globalThis.fetch = vi
      .fn()
      .mockRejectedValue(new Error('network down'));

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    expect(result.current.isAuthenticated).toBe(false);
    expect(result.current.error?.message).toContain('network down');
  });

  it('sends a bearer token from getToken when provided', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(sampleIdentity));
    globalThis.fetch = fetchMock;

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(
        'http://localhost:5000/graphql',
        undefined,
        () => 'tok-abc',
      ),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));

    const [, init] = fetchMock.mock.calls[0];
    expect(init.headers.Authorization).toBe('Bearer tok-abc');
  });

  it('refresh() refetches the session', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({}, 401))
      .mockResolvedValueOnce(jsonResponse(sampleIdentity));
    globalThis.fetch = fetchMock;

    const { result } = renderHook(() => useSession(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.isAuthenticated).toBe(false);

    result.current.refresh();

    await waitFor(() =>
      expect(result.current.isAuthenticated).toBe(true),
    );
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('useSession throws when used outside a SessionProvider', () => {
    const queryClient = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        {children}
      </QueryClientProvider>
    );

    expect(() => renderHook(() => useSession(), { wrapper })).toThrow(
      'useSession must be used within a SessionProvider',
    );
  });

  it('SessionProvider throws when used outside a BifrostProvider', () => {
    const queryClient = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>
        <SessionProvider>{children}</SessionProvider>
      </QueryClientProvider>
    );

    expect(() => renderHook(() => useSession(), { wrapper })).toThrow(
      'SessionProvider must be used within a BifrostProvider',
    );
  });
});
