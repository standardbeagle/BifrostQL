import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { BifrostProvider } from '@bifrostql/react';
import { SessionProvider } from '../auth/session-provider';
import { AppNav } from './app-nav';
import type { NavItem } from './app-nav';
import type { AppMetadata } from '../metadata/types';
import type { AppIdentity } from '../auth/session-context';

const ENDPOINT = 'http://localhost:5000/graphql';

/** Metadata overlay with three entities, each gated on `<key>.read`. */
const sampleMetadata: AppMetadata = {
  entities: {
    'dbo.users': { label: 'Users', icon: 'person', navPlacement: 'admin' },
    'dbo.orders': { label: 'Orders', icon: 'cart' },
    // `dbo.unlabeled` deliberately has no `label` — exercises key fallback.
    'dbo.unlabeled': {},
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

/**
 * Build a `fetch` mock that dispatches on URL path: the `/auth/session`
 * endpoint returns the supplied identity (or 401 when `null`), and the
 * `/_app-metadata` endpoint returns the supplied overlay.
 */
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

describe('AppNav', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('lists entities the session has permission for and hides the rest', async () => {
    // Arrange: session can read users + orders, but NOT dbo.unlabeled.
    globalThis.fetch = createFetchMock(
      identityWith(['dbo.users.read', 'dbo.orders.read']),
    );
    const Wrapper = createWrapper();
    let captured: NavItem[] = [];

    // Act
    render(
      <Wrapper>
        <AppNav>
          {(items) => {
            captured = items;
            return (
              <ul>
                {items.map((i) => (
                  <li key={i.key}>{i.label}</li>
                ))}
              </ul>
            );
          }}
        </AppNav>
      </Wrapper>,
    );

    // Assert: permitted entities render; the unpermitted one is hidden.
    await waitFor(() => expect(screen.getByText('Orders')).toBeInTheDocument());
    expect(screen.getByText('Users')).toBeInTheDocument();
    expect(screen.queryByText('dbo.unlabeled')).not.toBeInTheDocument();
    expect(captured.map((i) => i.key)).toEqual(['dbo.orders', 'dbo.users']);
  });

  it('renders no entries when the session has no matching permissions', async () => {
    // Arrange: authenticated, but holds an unrelated permission only.
    globalThis.fetch = createFetchMock(identityWith(['something.else']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppNav>{(items) => <div>count:{items.length}</div>}</AppNav>
      </Wrapper>,
    );

    // Assert
    await waitFor(() => expect(screen.getByText('count:0')).toBeInTheDocument());
  });

  it('supports a custom permission resolver', async () => {
    // Arrange: app uses `view:<entity>` instead of `<entity>.read`.
    globalThis.fetch = createFetchMock(identityWith(['view:dbo.users']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppNav permissionFor={(key) => `view:${key}`}>
          {(items) => <div>{items.map((i) => i.label).join(',')}</div>}
        </AppNav>
      </Wrapper>,
    );

    // Assert: only `dbo.users` matches the custom scheme.
    await waitFor(() => expect(screen.getByText('Users')).toBeInTheDocument());
  });

  it('renders a default <nav> with anchors when no render-prop is given', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['dbo.users.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppNav />
      </Wrapper>,
    );

    // Assert
    const link = await screen.findByRole('link', { name: 'Users' });
    expect(link).toHaveAttribute('href', '#/dbo.users');
  });

  it('falls back to the entity key as the label when label is unset', async () => {
    // Arrange: `dbo.unlabeled` has no `label`; grant permission for it.
    globalThis.fetch = createFetchMock(identityWith(['dbo.unlabeled.read']));
    const Wrapper = createWrapper();

    // Act
    render(
      <Wrapper>
        <AppNav>{(items) => <div>{items[0]?.label ?? 'none'}</div>}</AppNav>
      </Wrapper>,
    );

    // Assert
    await waitFor(() =>
      expect(screen.getByText('dbo.unlabeled')).toBeInTheDocument(),
    );
  });
});
