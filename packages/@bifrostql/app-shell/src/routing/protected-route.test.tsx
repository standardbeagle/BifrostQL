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

/** `fetch` mock for the `/auth/session` endpoint: identity, or 401 when null. */
function createSessionFetchMock(identity: AppIdentity | null) {
  return vi.fn((input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString();
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

  it('renders a 403 when authenticated but missing the required permission', async () => {
    // Arrange: authenticated, but lacks `dbo.users.read`.
    globalThis.fetch = createSessionFetchMock(identityWith(['other.perm']));
    const onUnauthenticated = vi.fn();
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute
          requirePermission="dbo.users.read"
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

  it('renders children when authenticated and permitted', async () => {
    // Arrange: authenticated with the required permission.
    globalThis.fetch = createSessionFetchMock(
      identityWith(['dbo.users.read']),
    );
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute requirePermission="dbo.users.read">
          <div>protected content</div>
        </ProtectedRoute>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('protected content')).toBeInTheDocument(),
    );
  });

  it('requires every permission when given an array', async () => {
    // Arrange: holds one of two required permissions.
    globalThis.fetch = createSessionFetchMock(
      identityWith(['dbo.users.read']),
    );
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <ProtectedRoute
          requirePermission={['dbo.users.read', 'dbo.users.write']}
        >
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

  it('allows an authenticated user when no permission is required', async () => {
    // Arrange: authenticated, no specific permission demanded.
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
    // Arrange
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
});
