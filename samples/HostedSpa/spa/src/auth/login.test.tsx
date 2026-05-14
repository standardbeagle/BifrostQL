import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PathProvider } from '@standardbeagle/virtual-router';
import { AppShellProvider } from '@bifrostql/app-shell';
import { Login } from './login';

const ENDPOINT = 'http://localhost:5000/graphql';

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
 * Builds a fetch mock answering `/auth/session` (unauthenticated until the
 * `sessionAuthenticated` ref flips) and `/auth/login` (success or failure as
 * configured). The login POST flips the session ref so a follow-up
 * `/auth/session` refetch resolves authenticated, mirroring the cookie flow.
 */
function createFetchMock(options: { loginSucceeds: boolean }) {
  const state = { authenticated: false };
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString();

    if (url.includes('/auth/session')) {
      if (!state.authenticated) {
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
        json: () => Promise.resolve(identityWith(['main.members.read'])),
      } as Response);
    }

    if (url.includes('/auth/login')) {
      if (init?.method !== 'POST') {
        return Promise.reject(new Error(`unexpected method for ${url}`));
      }
      if (options.loginSucceeds) {
        state.authenticated = true;
        return Promise.resolve({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => Promise.resolve(identityWith(['main.members.read'])),
        } as Response);
      }
      return Promise.resolve({
        ok: false,
        status: 401,
        statusText: 'Unauthorized',
        json: () => Promise.resolve({ error: 'Invalid credentials' }),
      } as Response);
    }

    return Promise.reject(new Error(`unexpected fetch: ${url}`));
  });
  return fetchMock;
}

function renderLogin() {
  return render(
    <PathProvider path="/login">
      <AppShellProvider config={{ endpoint: ENDPOINT }}>
        <Login />
      </AppShellProvider>
    </PathProvider>,
  );
}

describe('Login', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('renders the login screen with onboarding guidance', async () => {
    // Arrange
    globalThis.fetch = createFetchMock({ loginSucceeds: true });

    // Act
    renderLogin();

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('login-screen')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText(/username/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/password/i)).toBeInTheDocument();
    expect(screen.getByTestId('onboarding')).toBeInTheDocument();
  });

  it('surfaces an error when login fails', async () => {
    // Arrange
    globalThis.fetch = createFetchMock({ loginSucceeds: false });
    renderLogin();
    await waitFor(() =>
      expect(screen.getByTestId('login-screen')).toBeInTheDocument(),
    );

    // Act
    await userEvent.type(screen.getByLabelText(/username/i), 'admin');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    // Assert
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/invalid/i),
    );
  });

  it('posts credentials to /auth/login on submit', async () => {
    // Arrange
    const fetchMock = createFetchMock({ loginSucceeds: true });
    globalThis.fetch = fetchMock;
    renderLogin();
    await waitFor(() =>
      expect(screen.getByTestId('login-screen')).toBeInTheDocument(),
    );

    // Act
    await userEvent.type(screen.getByLabelText(/username/i), 'admin');
    await userEvent.type(screen.getByLabelText(/password/i), 'secret');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    // Assert: a POST reached /auth/login carrying the typed credentials.
    await waitFor(() => {
      const loginCall = fetchMock.mock.calls.find(([url]) =>
        String(url).includes('/auth/login'),
      );
      expect(loginCall).toBeDefined();
      const init = loginCall?.[1] as RequestInit;
      expect(init.method).toBe('POST');
      expect(init.credentials).toBe('include');
      expect(JSON.parse(init.body as string)).toEqual({
        username: 'admin',
        password: 'secret',
      });
    });
  });
});
