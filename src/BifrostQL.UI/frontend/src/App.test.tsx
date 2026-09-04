// @vitest-environment jsdom
/**
 * `loadSession()` sat in App's render body, so every single render re-ran its
 * read-parse-sanitize-**write** cycle against sessionStorage and produced a
 * fresh `restored` object identity — fed into useConnectionFlows, where it is
 * only ever meaningful on mount. A lazy state initializer runs it exactly once.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { ConnectionInfo } from './connection/types';

const session = vi.hoisted(() => ({
  loadSession: vi.fn<() => ConnectionInfo | null>(() => null),
  saveSession: vi.fn(),
}));
vi.mock('./connection/session', () => session);


// The real editor is expensive to mount, but its ONE behaviour that matters
// here is the schema query: `useSchema` runs on a per-mount QueryClient with
// `staleTime: Infinity`, so it fires exactly once per mount, through whatever
// fetcher the shell handed that mount. The stub reproduces that — a
// mount-only query — so a mount carrying the previous profile's transport
// shows up as a request to the previous profile's URL. Everything else the
// app imports from the package is kept real.
vi.mock('@standardbeagle/edit-db', async (importOriginal) => {
  const React = await import('react');
  return {
    ...(await importOriginal<Record<string, unknown>>()),
    default: ({ fetcher }: { fetcher?: { query: (q: string) => Promise<unknown> } }) => {
      React.useEffect(() => {
        void fetcher?.query('{ _dbSchema { dbName } }').catch(() => undefined);
        // Mount-only, matching the editor's per-mount schema query.
        // eslint-disable-next-line react-hooks/exhaustive-deps
      }, []);
      return null;
    },
  };
});

// Statically imported: pulling App (and the edit-db bundle behind it) in from
// inside the test body counts its load time against the per-test timeout.
import App from './App';
import { PROFILE_STORAGE_KEY } from './profiles/api-profiles';

const CONNECTED: ConnectionInfo = {
  id: 'conn-1',
  name: 'Test connection',
  connectionString: '',
  connectedAt: '2026-01-01T00:00:00.000Z',
  server: 'localhost',
  database: 'testdb',
  provider: 'sqlserver',
};

// Two profiles with DIFFERENT endpoints: a single-profile fixture cannot
// distinguish "queried the new profile" from "queried anything at all".
const PROFILES = [
  { id: 'default', label: 'Database (raw)', serverProfile: null },
  { id: 'reporting', label: 'Reporting', serverProfile: 'reporting' },
];

const rawGraphqlUrl = () => `${window.location.origin}/graphql`;

/**
 * Installs a fetch stub that answers the profile list and any GraphQL POST,
 * and returns the list of GraphQL URLs requested, in order.
 */
function installFetch(): string[] {
  const graphqlUrls: string[] = [];
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes('/api/profiles')) {
      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(PROFILES) } as Response);
    }
    if (url.includes('/graphql')) {
      graphqlUrls.push(url);
      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ data: {} }) } as Response);
    }
    return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) } as Response);
  }));
  return graphqlUrls;
}

beforeEach(() => {
  session.loadSession.mockClear();
  session.loadSession.mockReturnValue(null);
  window.localStorage.clear();
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) } as Response)));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('App session restore', () => {
  it('reads the stored session once, not on every render', () => {
    render(<App />);

    const afterMount = session.loadSession.mock.calls.length;
    expect(afterMount).toBe(1);

    // Any state change re-renders App; the session must not be re-read.
    fireEvent.click(screen.getByTestId('connect-card'));

    expect(session.loadSession.mock.calls.length).toBe(afterMount);
  });
});

/**
 * H13. The shell changes `graphqlPath` and remounts the editor in one batch,
 * but the transport behind the fetcher is rebuilt in an effect that runs
 * afterwards. The remounted editor therefore issued its once-per-mount schema
 * query over the PREVIOUS profile's transport and cached that answer forever
 * (staleTime Infinity), while later table queries went to the new profile —
 * profile A's schema over profile B's data.
 *
 * The negative assertion is the load-bearing half: a test that only checks
 * that the new URL is eventually hit passes on the buggy code, which hits the
 * old URL first and the new one later.
 */
describe('App profile switching', () => {
  it('sends the schema query only to the newly selected profile', async () => {
    session.loadSession.mockReturnValue(CONNECTED);
    const graphqlUrls = installFetch();

    render(<App />);

    // Wait for the two-profile list to land: the picker is disabled while the
    // app still holds the single-entry fallback.
    await screen.findByRole('option', { name: 'Reporting' });
    await waitFor(() => expect(graphqlUrls.length).toBeGreaterThan(0));
    const beforeSwitch = graphqlUrls.length;
    expect(graphqlUrls[0]).toBe(rawGraphqlUrl());

    fireEvent.change(screen.getByLabelText('Profile'), { target: { value: 'reporting' } });

    await waitFor(() => expect(graphqlUrls.length).toBeGreaterThan(beforeSwitch));
    const afterSwitch = graphqlUrls.slice(beforeSwitch);
    expect(afterSwitch).not.toContain(rawGraphqlUrl());
    expect(afterSwitch.every((url) => url.includes('profile=reporting'))).toBe(true);
  });

  it('sends the FIRST schema query to a persisted non-default profile', async () => {
    // Same race at startup: the persisted id is not resolvable until the
    // profile list arrives, so the editor used to mount on the raw default and
    // cache its schema before the real profile was known.
    window.localStorage.setItem(PROFILE_STORAGE_KEY, 'reporting');
    session.loadSession.mockReturnValue(CONNECTED);
    const graphqlUrls = installFetch();

    render(<App />);

    await waitFor(() => expect(graphqlUrls.length).toBeGreaterThan(0));
    expect(graphqlUrls[0]).toContain('profile=reporting');
    expect(graphqlUrls).not.toContain(rawGraphqlUrl());
  });
});
