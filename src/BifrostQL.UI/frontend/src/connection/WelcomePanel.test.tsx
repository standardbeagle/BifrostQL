// @vitest-environment jsdom
/**
 * Removing ONE recent connection must not wipe the rest. The delete button used
 * to persist the filtered remainder itself and then call the parent's
 * "clear all" callback, whose owner wrote an empty list over the top — so a
 * single click on any `×` destroyed every recent connection, in the UI and in
 * localStorage, unrecoverably. The panel now reports the single removal to its
 * owner and never writes storage itself.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { WelcomePanel } from './WelcomePanel';
import { loadRecentConnections, saveRecentConnections } from './recent-connections';
import type { ConnectionInfo } from './types';

const conn = (id: string, name: string): ConnectionInfo => ({
  id,
  name,
  connectionString: `Data Source=/tmp/${id}.db`,
  connectedAt: new Date().toISOString(),
  server: 'localhost',
  database: name,
  provider: 'sqlite',
});

describe('WelcomePanel recent connections', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false } as Response)));
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('reports a single removal to the owner without clearing the whole list', () => {
    const all = [conn('1', 'alpha'), conn('2', 'beta'), conn('3', 'gamma')];
    saveRecentConnections(all);

    const onRemoveRecentConnection = vi.fn();
    const onClearRecentConnections = vi.fn();

    render(
      <WelcomePanel
        onConnectClick={() => {}}
        onQuickStart={() => {}}
        recentConnections={all}
        onRemoveRecentConnection={onRemoveRecentConnection}
        onClearRecentConnections={onClearRecentConnections}
      />,
    );

    fireEvent.click(screen.getByLabelText('Remove beta'));

    // The owner is told exactly which entry went away...
    expect(onRemoveRecentConnection).toHaveBeenCalledExactlyOnceWith('2');
    // ...and the "clear all" path stays untouched, so the owner never
    // overwrites storage with an empty list.
    expect(onClearRecentConnections).not.toHaveBeenCalled();
    // The panel does not persist on its own; the other two survive.
    expect(loadRecentConnections().map((c) => c.id)).toEqual(['1', '2', '3']);
  });

  it('manages its own list when the owner supplies none', () => {
    saveRecentConnections([conn('1', 'alpha'), conn('2', 'beta')]);

    render(<WelcomePanel onConnectClick={() => {}} onQuickStart={() => {}} />);

    fireEvent.click(screen.getByLabelText('Remove alpha'));

    expect(screen.queryByLabelText('Remove alpha')).toBeNull();
    expect(loadRecentConnections().map((c) => c.id)).toEqual(['2']);
  });
});
