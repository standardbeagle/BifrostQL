import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { SessionProvider } from '../auth/session-provider';
import { ProtectedRoute } from './protected-route';
import type { AppIdentity } from '../auth/session-context';

const ENDPOINT = 'http://localhost:5000/graphql';

function identityWith(permissions: string[]): AppIdentity {
  return {
    id: 'user-1',
    provider: 'local',
    email: 'user@example.com',
    displayName: 'Test User',
    orgIds: [],
    roles: [],
    permissions,
    claims: {},
  };
}

/**
 * `fetch` mock for `/auth/session` (identity, or 401 when null) and for the
 * GraphQL endpoint, which answers the policy query with `grants` as the
 * server-resolved `_grants` list. The session identity's `permissions` are
 * deliberately separate from `grants`: the route must read only the latter.
 */
function createSessionFetchMock(
  identity: AppIdentity | null,
  grants: string[] = [],
) {
  return vi.fn((input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString();
    if (url === ENDPOINT) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({ data: { _grants: grants } }),
      } as Response);
    }
    if (url.includes('/auth/session')) {
      if (identity === null) {
        return Promise.resolve({
          ok: false,
          status: 401,
          statusText: 'Unauthorized',
          json: () => Promise.resolve(null),
        } as Response);
      }
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve(identity),
      } as Response);
    }
    return Promise.reject(new Error(`Unexpected fetch: ${url}`));
  });
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <BifrostProvider config={{ endpoint: ENDPOINT }}>
          <SessionProvider>{children}</SessionProvider>
        </BifrostProvider>
      </QueryClientProvider>
    );
  };
}

describe('ProtectedRoute', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('redirects unauthenticated users via onUnauthenticated and hides children', async () => {
    // Arrange: no session (401).
    globalThis.fetch = createSessionFetchMock(null);
    const onUnauthenticated = vi.fn();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute onUnauthenticated={onUnauthenticated}>
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: redirect fired exactly once; protected content never rendered.
    await waitFor(() => expect(onUnauthenticated).toHaveBeenCalledTimes(1));
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
  });

  it('renders a 403 when authenticated but the server grants lack the requirement', async () => {
    // Arrange: authenticated; the server resolves `other.grant` only.
    globalThis.fetch = createSessionFetchMock(identityWith([]), [
      'other.grant',
    ]);
    const onUnauthenticated = vi.fn();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute
          requiredGrants="dbo.users.read"
          onUnauthenticated={onUnauthenticated}
        >
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: 403 alert shown, content hidden, no redirect (user IS authed).
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('403'),
    );
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
    expect(onUnauthenticated).not.toHaveBeenCalled();
  });

  it('renders children when the server resolves the required grant', async () => {
    // Arrange: the session carries no permissions at all; only `_grants` does.
    globalThis.fetch = createSessionFetchMock(identityWith([]), [
      'dbo.users.read',
    ]);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute requiredGrants="dbo.users.read">
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('protected content')).toBeInTheDocument(),
    );
  });

  it('ignores session permissions that the server does not grant', async () => {
    // Arrange: the session claims the permission, the server resolves nothing.
    globalThis.fetch = createSessionFetchMock(
      identityWith(['dbo.users.read']),
      [],
    );
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute requiredGrants="dbo.users.read">
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: a client-side claim is not an authorization answer.
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('403'),
    );
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
  });

  it('renders the loading fallback until the server grants arrive', async () => {
    // Arrange: authenticated; the grants answer is held open.
    let resolveGrants: (grants: string[]) => void = () => {};
    const grantsAnswer = new Promise<string[]>((resolve) => {
      resolveGrants = resolve;
    });
    const sessionFetch = createSessionFetchMock(identityWith([]));
    globalThis.fetch = vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url === ENDPOINT) {
        return grantsAnswer.then(
          (grants) =>
            ({
              ok: true,
              status: 200,
              statusText: 'OK',
              json: () => Promise.resolve({ data: { _grants: grants } }),
            }) as Response,
        );
      }
      return sessionFetch(input);
    });
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute
          requiredGrants="dbo.users.read"
          loadingFallback={<div>loading</div>}
        >
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: neither content nor 403 flashes before the server answers.
    await waitFor(() =>
      expect(screen.getByText('loading')).toBeInTheDocument(),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
    resolveGrants(['dbo.users.read']);
    await waitFor(() =>
      expect(screen.getByText('protected content')).toBeInTheDocument(),
    );
  });

  it('requires every grant when given an array', async () => {
    // Arrange: the server resolves one of two required grants.
    globalThis.fetch = createSessionFetchMock(identityWith([]), [
      'dbo.users.read',
    ]);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute requiredGrants={['dbo.users.read', 'dbo.users.write']}>
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: missing `dbo.users.write` -> 403.
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('403'),
    );
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
  });

  it('allows an authenticated user when no grant is required', async () => {
    // Arrange: authenticated, no specific grant demanded.
    globalThis.fetch = createSessionFetchMock(identityWith([]));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute>
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('protected content')).toBeInTheDocument(),
    );
  });

  it('renders the custom forbidden fallback when supplied', async () => {
    // Arrange: the deprecated `requirePermission` alias gates on grants too.
    globalThis.fetch = createSessionFetchMock(identityWith([]));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute
          requirePermission="dbo.users.read"
          forbiddenFallback={<div>custom forbidden</div>}
        >
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('custom forbidden')).toBeInTheDocument(),
    );
  });

  it('fires onUnauthenticated once even when the callback identity changes', async () => {
    // Arrange: the real-world call-site shape is an inline arrow, so the
    // callback is a brand-new function on every render.
    globalThis.fetch = createSessionFetchMock(null);
    const Wrapper = createWrapper();
    const navigate = vi.fn();

    function Screen() {
      return (
        <ProtectedRoute onUnauthenticated={() => navigate('/login')}>
          <div>protected content</div>
        </ProtectedRoute>
      );
    }

    const { rerender } = render(
      <Wrapper>
        <Screen />
      </Wrapper>,
    );
    await waitFor(() => expect(navigate).toHaveBeenCalledTimes(1));

    // Act: re-render with the same auth state and a fresh closure.
    rerender(
      <Wrapper>
        <Screen />
      </Wrapper>,
    );
    rerender(
      <Wrapper>
        <Screen />
      </Wrapper>,
    );

    // Assert: the redirect fires on the auth transition, not on renders.
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('uses the latest onUnauthenticated when the redirect finally fires', async () => {
    // Arrange: holding the callback in a ref must not pin a stale one.
    globalThis.fetch = createSessionFetchMock(null);
    const Wrapper = createWrapper();
    const stale = vi.fn();
    const fresh = vi.fn();

    const { rerender } = render(
      <Wrapper>
        <ProtectedRoute onUnauthenticated={stale}>
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );
    await waitFor(() => expect(stale).toHaveBeenCalledTimes(1));

    // Act
    rerender(
      <Wrapper>
        <ProtectedRoute onUnauthenticated={fresh}>
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert: no extra call from the swap, and the ref now holds `fresh`.
    expect(fresh).not.toHaveBeenCalled();
    expect(stale).toHaveBeenCalledTimes(1);
  });
});
