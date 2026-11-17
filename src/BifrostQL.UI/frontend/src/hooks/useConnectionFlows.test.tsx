// @vitest-environment jsdom
/**
 * Recent-connection ownership lives in this hook: it holds the list state AND
 * the localStorage write. Two behaviours are pinned here because both used to
 * destroy user data silently — removing one entry must not clear the rest, and
 * a freshly launched quickstart must not be the entry that gets trimmed away.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useConnectionFlows } from './useConnectionFlows';
import {
  loadRecentConnections,
  saveRecentConnections,
  MAX_RECENT_CONNECTIONS,
} from '../connection/recent-connections';
import type { ConnectionInfo } from '../connection/types';
import { loadSession, saveSession } from '../connection/session';

const conn = (id: string, name: string): ConnectionInfo => ({
  id,
  name,
  connectionString: `Data Source=/tmp/${id}.db`,
  connectedAt: new Date().toISOString(),
  server: 'localhost',
  database: name,
  provider: 'sqlite',
});

describe('useConnectionFlows recent connections', () => {
  beforeEach(() => {
    localStorage.clear();
    // fetchVaultServers fires on mount; keep it inert.
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ servers: [] }) } as Response)),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  const mount = () =>
    renderHook(() => useConnectionFlows({ restored: null, enterEditor: () => {}, onRestoreFailed: () => {} }));

  it('removes one recent connection and keeps the others', async () => {
    saveRecentConnections([conn('1', 'alpha'), conn('2', 'beta'), conn('3', 'gamma')]);

    const { result } = mount();
    await waitFor(() => expect(result.current.recentConnections).toHaveLength(3));

    act(() => result.current.handleRemoveRecentConnection('2'));

    expect(result.current.recentConnections.map((c) => c.id)).toEqual(['1', '3']);
    expect(loadRecentConnections().map((c) => c.id)).toEqual(['1', '3']);
  });

  it('still clears every recent connection on the clear-all path', async () => {
    saveRecentConnections([conn('1', 'alpha'), conn('2', 'beta')]);

    const { result } = mount();
    await waitFor(() => expect(result.current.recentConnections).toHaveLength(2));

    act(() => result.current.handleClearRecentConnections());

    expect(result.current.recentConnections).toEqual([]);
    expect(loadRecentConnections()).toEqual([]);
  });

  it('keeps a newly launched quickstart when the recents list is already full', async () => {
    saveRecentConnections(
      Array.from({ length: MAX_RECENT_CONNECTIONS }, (_, i) => conn(`old-${i}`, `old-${i}`)),
    );

    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        if (String(input).includes('/api/database/create-quickstart')) {
          return Promise.resolve({
            ok: true,
            headers: new Headers({ 'content-type': 'application/json' }),
            json: () => Promise.resolve({ connectionString: 'Data Source=/tmp/new.db' }),
          } as unknown as Response);
        }
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({ servers: [] }),
        } as Response);
      }),
    );

    const { result } = mount();
    await waitFor(() =>
      expect(result.current.recentConnections).toHaveLength(MAX_RECENT_CONNECTIONS),
    );

    await act(async () => {
      await result.current.handleQuickStartLaunch('blog', 'sample');
    });

    // The just-created database must survive: it used to be appended past the
    // cap and then sliced straight back off, so the one connection the user
    // actually asked for was the one that never made it into the list.
    expect(result.current.recentConnections).toHaveLength(MAX_RECENT_CONNECTIONS);
    expect(result.current.recentConnections[0].connectionString).toBe('Data Source=/tmp/new.db');
    expect(loadRecentConnections()[0].connectionString).toBe('Data Source=/tmp/new.db');
  });
});

describe('useConnectionFlows vault session restore', () => {
  const restoredVault: ConnectionInfo = { ...conn('v', 'prod'), provider: 'postgres', vaultServerName: 'prod-pg' };

  afterEach(() => {
    vi.unstubAllGlobals();
    sessionStorage.clear();
  });

  function stubVaultConnect(reply: () => Promise<Response>) {
    const fetchMock = vi.fn((input: RequestInfo | URL) =>
      String(input).includes('/api/vault/connect')
        ? reply()
        : Promise.resolve({ ok: true, json: () => Promise.resolve({ servers: [] }) } as Response),
    );
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('holds the session in connecting until the server re-activates the vault entry', async () => {
    let resolveConnect!: (r: Response) => void;
    stubVaultConnect(() => new Promise<Response>((resolve) => { resolveConnect = resolve; }));

    const { result } = renderHook(() =>
      useConnectionFlows({ restored: restoredVault, enterEditor: () => {}, onRestoreFailed: () => {} }));

    expect(result.current.connectionState).toBe('connecting');
    await waitFor(() => expect(resolveConnect).toBeTypeOf('function'));
    await act(async () => {
      resolveConnect({ ok: true, json: () => Promise.resolve({ success: true }) } as Response);
    });
    await waitFor(() => expect(result.current.connectionState).toBe('connected'));
  });

  it('surfaces a failed restore, clears the session and leaves the editor', async () => {
    stubVaultConnect(() => Promise.resolve({ ok: false, json: () => Promise.resolve({ success: false, error: 'password expired' }) } as Response));
    saveSession(restoredVault);
    expect(loadSession()).not.toBeNull();
    const onRestoreFailed = vi.fn();

    const { result } = renderHook(() =>
      useConnectionFlows({ restored: restoredVault, enterEditor: () => {}, onRestoreFailed }));

    await waitFor(() => expect(onRestoreFailed).toHaveBeenCalledTimes(1));
    expect(result.current.connectionState).toBe('error');
    expect(result.current.errorMessage).toContain('prod-pg');
    expect(result.current.errorMessage).toContain('password expired');
    expect(result.current.connectionInfo).toBeNull();
    expect(loadSession()).toBeNull();
  });
});

describe('useConnectionFlows SQLite file binding', () => {
  // The SQLite connect used to call only a connection test, so the header named
  // the file while the server kept serving the previous database (or nothing).
  type Call = { url: string; body: unknown };
  let calls: Call[];

  beforeEach(() => {
    localStorage.clear();
    calls = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
        if (url.includes('/api/sqlite/connect')) {
          return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({ success: true, database: 'app.db', connectionString: 'Data Source=/data/app.db;Mode=ReadWrite' }),
          } as Response);
        }
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ success: true, servers: [] }) } as Response);
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    sessionStorage.clear();
  });

  const bindCalls = () => calls.filter((c) => c.url.includes('/api/sqlite/connect'));

  it('binds a form-mode file with its createNew flag and stores the host-built string', async () => {
    const enterEditor = vi.fn();
    const { result } = renderHook(() => useConnectionFlows({ restored: null, enterEditor, onRestoreFailed: () => {} }));

    await act(async () => {
      await result.current.handleConnect({ name: 'app.db', provider: 'sqlite', filePath: '/data/app.db', createNew: true });
    });

    expect(bindCalls().map((c) => c.body)).toEqual([{ filePath: '/data/app.db', createNew: true }]);
    expect(calls.some((c) => c.url.includes('/api/connection/test'))).toBe(false);
    expect(result.current.connectionInfo?.connectionString).toBe('Data Source=/data/app.db;Mode=ReadWrite');
    expect(enterEditor).toHaveBeenCalledTimes(1);
  });

  it('binds a recent SQLite connection by its stored connection string', async () => {
    const { result } = renderHook(() => useConnectionFlows({ restored: null, enterEditor: () => {}, onRestoreFailed: () => {} }));

    await act(async () => {
      result.current.handleSelectRecentConnection(conn('r', 'recent'));
    });

    await waitFor(() => expect(bindCalls().map((c) => c.body)).toEqual([{ connectionString: 'Data Source=/tmp/r.db' }]));
  });

  it('re-binds a restored SQLite session and holds it in connecting until then', async () => {
    const restoredSqlite = conn('s', 'saved');

    const { result } = renderHook(() =>
      useConnectionFlows({ restored: restoredSqlite, enterEditor: () => {}, onRestoreFailed: () => {} }));

    expect(result.current.connectionState).toBe('connecting');
    await waitFor(() => expect(result.current.connectionState).toBe('connected'));
    expect(bindCalls().map((c) => c.body)).toEqual([{ connectionString: 'Data Source=/tmp/s.db' }]);
  });
});
