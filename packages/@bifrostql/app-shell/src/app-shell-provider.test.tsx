import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useContext } from 'react';
import type { ReactNode } from 'react';
import { BifrostContext } from '@bifrostql/react';
import { AppShellProvider } from './app-shell-provider';
import { useSession } from './auth/use-session';
import type { AppIdentity } from './auth/session-context';

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? 'OK' : 'Error',
    json: () => Promise.resolve(body),
  } as Response;
}

const sampleIdentity: AppIdentity = {
  id: 'user-1',
  provider: 'local',
  orgIds: [],
  roles: [],
  permissions: ['reports.view'],
  claims: {},
};

describe('AppShellProvider', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it('wires a hosted-mode BifrostProvider plus SessionProvider from one config', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(sampleIdentity));

    const wrapper = ({ children }: { children: ReactNode }) => (
      <AppShellProvider config={{ endpoint: 'http://localhost:7000/graphql' }}>
        {children}
      </AppShellProvider>
    );

    const { result } = renderHook(
      () => ({
        bifrost: useContext(BifrostContext),
        session: useSession(),
      }),
      { wrapper },
    );

    // BifrostProvider is wired: the GraphQL endpoint is exposed via context.
    expect(result.current.bifrost?.endpoint).toBe(
      'http://localhost:7000/graphql',
    );

    // SessionProvider is wired: the session bootstraps beneath it.
    await waitFor(() =>
      expect(result.current.session.isLoading).toBe(false),
    );
    expect(result.current.session.isAuthenticated).toBe(true);
    expect(result.current.session.permissions).toEqual(['reports.view']);
  });

  it('infers the hosted-mode endpoint from window.origin when none is given', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}, 401));
    globalThis.fetch = fetchMock;

    const wrapper = ({ children }: { children: ReactNode }) => (
      <AppShellProvider>{children}</AppShellProvider>
    );

    const { result } = renderHook(() => useContext(BifrostContext), {
      wrapper,
    });

    expect(result.current?.endpoint).toBe(
      `${window.location.origin}/graphql`,
    );

    await waitFor(() => {
      const sessionUrl = fetchMock.mock.calls[0]?.[0];
      expect(sessionUrl).toBe(`${window.location.origin}/auth/session`);
    });
  });
});
