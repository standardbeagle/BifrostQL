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
import { loadRecentConnections, saveRecentConnections } from '../connection/recent-connections';
import type { ConnectionInfo } from '../connection/types';

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
    renderHook(() => useConnectionFlows({ restored: null, enterEditor: () => {} }));

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

});
