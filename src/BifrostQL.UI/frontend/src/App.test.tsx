// @vitest-environment jsdom
/**
 * `loadSession()` sat in App's render body, so every single render re-ran its
 * read-parse-sanitize-**write** cycle against sessionStorage and produced a
 * fresh `restored` object identity — fed into useConnectionFlows, where it is
 * only ever meaningful on mount. A lazy state initializer runs it exactly once.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';

const session = vi.hoisted(() => ({ loadSession: vi.fn(() => null), saveSession: vi.fn() }));
vi.mock('./connection/session', () => session);


// The embedded editor is irrelevant here and expensive to mount; everything
// else the app imports from the package is kept real.
vi.mock('@standardbeagle/edit-db', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  default: () => null,
}));

// Statically imported: pulling App (and the edit-db bundle behind it) in from
// inside the test body counts its load time against the per-test timeout.
import App from './App';

beforeEach(() => {
  session.loadSession.mockClear();
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) } as Response)));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
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
