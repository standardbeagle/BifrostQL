import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { SessionProvider } from '../auth/session-provider';
import { AppLayout } from './app-layout';
import type { AppMetadata } from '../metadata/types';
import type { AppIdentity } from '../auth/session-context';

const ENDPOINT = 'http://localhost:5000/graphql';

const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': { label: 'Users' },
  },
};

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

function createFetchMock(
  identity: AppIdentity | null,
  metadata: AppMetadata = sampleMetadata,
) {
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
    if (url.includes('/_app-metadata')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve(metadata),
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

describe('AppLayout', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('composes the permission-aware nav with the routed content outlet', async () => {
    // Arrange: authenticated user permitted to see `dbo.users`.
    globalThis.fetch = createFetchMock(identityWith(['dbo.users.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppLayout>
          <div>routed page</div>
        </AppLayout>
      </Wrapper>,
    );

    // Assert: both the nav entry and the outlet content render.
    await waitFor(() => expect(screen.getByText('Users')).toBeInTheDocument());
    expect(screen.getByText('routed page')).toBeInTheDocument();
    expect(
      screen.getByRole('navigation', { name: 'Application navigation' }),
    ).toBeInTheDocument();
  });

  it('shows an auth-aware identity summary when authenticated', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['dbo.users.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppLayout>
          <div>routed page</div>
        </AppLayout>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('app-layout-identity')).toHaveTextContent(
        'Test User',
      ),
    );
  });

  it('omits the identity summary when unauthenticated', async () => {
    // Arrange: no session.
    globalThis.fetch = createFetchMock(null);
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppLayout>
          <div>routed page</div>
        </AppLayout>
      </Wrapper>,
    );

    // Assert: outlet still renders, identity chrome does not.
    await waitFor(() =>
      expect(screen.getByText('routed page')).toBeInTheDocument(),
    );
    expect(
      screen.queryByTestId('app-layout-identity'),
    ).not.toBeInTheDocument();
  });

  it('renders a custom nav override instead of the default AppNav', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['dbo.users.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppLayout nav={<div>custom nav</div>}>
          <div>routed page</div>
        </AppLayout>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('custom nav')).toBeInTheDocument(),
    );
    expect(
      screen.queryByRole('navigation', { name: 'Application navigation' }),
    ).not.toBeInTheDocument();
  });

  it('renders optional header chrome', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['dbo.users.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppLayout header={<div>Brand</div>}>
          <div>routed page</div>
        </AppLayout>
      </Wrapper>,
    );

    // Assert
    await waitFor(() => expect(screen.getByText('Brand')).toBeInTheDocument());
  });
});
