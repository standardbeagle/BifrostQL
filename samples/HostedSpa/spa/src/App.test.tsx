import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import App from './App';

/** Identity contract mirror — see app-shell `AppIdentity`. */
interface TestIdentity {
  id: string;
  provider: string;
  email: string;
  displayName: string;
  orgIds: string[];
  roles: string[];
  permissions: string[];
  claims: Record<string, unknown>;
}

function identityWith(permissions: string[]): TestIdentity {
  return {
    id: 'user-1',
    provider: 'local',
    email: 'admin@example.com',
    displayName: 'Seeded Admin',
    orgIds: [],
    roles: [],
    permissions,
    claims: {},
  };
}

/**
 * Builds a fetch mock answering `/auth/session`, `/auth/logout`, the
 * app-metadata probe, and GraphQL queries with empty fixtures. The session is
 * authenticated or not per the `identity` argument.
 */
function createFetchMock(identity: TestIdentity | null) {
  return vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
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

    if (url.includes('/auth/logout')) {
      if (init?.method !== 'POST') {
        return Promise.reject(new Error(`unexpected method for ${url}`));
      }
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({}),
      } as Response);
    }

    if (url.includes('/_app-metadata')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.resolve({ entities: {} }),
      } as Response);
    }

    // GraphQL: empty result for every query.
    return Promise.resolve({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: () => Promise.resolve({ data: {} }),
    } as Response);
  });
}

describe('App auth gating', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the login screen for an unauthenticated session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(null);

    // Act
    render(
      <PathProvider path="/">
        <App />
      </PathProvider>,
    );

    // Assert: the gate redirects to the login screen.
    await waitFor(() =>
      expect(screen.getByTestId('login-screen')).toBeInTheDocument(),
    );
  });

  it('renders the app and exposes logout for an authenticated session', async () => {
    // Arrange
    globalThis.fetch = createFetchMock(identityWith(['main.members.read']));

    // Act
    render(
      <PathProvider path="/">
        <App />
      </PathProvider>,
    );

    // Assert: the app chrome renders and a logout control is present.
    await waitFor(() =>
      expect(screen.getByTestId('app-layout-identity')).toBeInTheDocument(),
    );
    expect(
      screen.getByRole('button', { name: /log out/i }),
    ).toBeInTheDocument();
    expect(screen.queryByTestId('login-screen')).not.toBeInTheDocument();
  });

  it('posts to /auth/logout when the logout control is used', async () => {
    // Arrange
    const fetchMock = createFetchMock(identityWith(['main.members.read']));
    globalThis.fetch = fetchMock;
    render(
      <PathProvider path="/">
        <App />
      </PathProvider>,
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: /log out/i }),
      ).toBeInTheDocument(),
    );

    // Act
    await userEvent.click(screen.getByRole('button', { name: /log out/i }));

    // Assert
    await waitFor(() => {
      const logoutCall = fetchMock.mock.calls.find(([url]) =>
        String(url).includes('/auth/logout'),
      );
      expect(logoutCall).toBeDefined();
      expect((logoutCall?.[1] as RequestInit).method).toBe('POST');
    });
  });
});
