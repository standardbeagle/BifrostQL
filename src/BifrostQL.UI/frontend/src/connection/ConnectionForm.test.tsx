// @vitest-environment jsdom
/**
 * A connect attempt that does not succeed must hand the form back to the user.
 * handleConnect used to set 'connecting' and call onConnect without awaiting
 * it, so nothing ever restored 'idle': a rejected connect, or the routine act
 * of pressing Escape in the credential prompt, left every input and both
 * buttons permanently disabled. The only way out was Back, which discards the
 * host/port/database/SSH details the user had just typed.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ConnectionForm } from './ConnectionForm';
import { CredentialCancelledError } from '../lib/credential-prompt';

afterEach(cleanup);

const databaseInput = () => screen.getByLabelText(/Database Name/i) as HTMLInputElement;
const usernameInput = () => screen.getByLabelText(/Username/i) as HTMLInputElement;
const connectButton = () => screen.getByRole('button', { name: /^Connect$/ }) as HTMLButtonElement;

/** Fills the required SQL Server fields so Connect passes validation. */
function fillRequiredFields() {
  fireEvent.change(databaseInput(), { target: { value: 'inventory' } });
  fireEvent.change(usernameInput(), { target: { value: 'sa' } });
}

function renderForm(onConnect: () => Promise<void>) {
  render(<ConnectionForm provider="sqlserver" onConnect={onConnect} onBack={() => {}} />);
  fillRequiredFields();
  fireEvent.click(connectButton());
}

describe('ConnectionForm connect lifecycle', () => {
  it('re-enables the form after a failed connect, keeping the typed input', async () => {
    renderForm(vi.fn(() => Promise.reject(new Error('host unreachable'))));

    await waitFor(() => expect(connectButton().disabled).toBe(false));
    expect(databaseInput().disabled).toBe(false);
    expect(databaseInput().value).toBe('inventory');
    expect(usernameInput().value).toBe('sa');
  });

  it('surfaces the failure so the user knows why nothing happened', async () => {
    renderForm(vi.fn(() => Promise.reject(new Error('host unreachable'))));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('host unreachable');
  });

  it('returns to a usable form when the credential prompt is cancelled', async () => {
    renderForm(vi.fn(() => Promise.reject(new CredentialCancelledError())));

    await waitFor(() => expect(connectButton().disabled).toBe(false));
    expect(databaseInput().value).toBe('inventory');
    // Cancelling is a routine choice, not an error worth shouting about.
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('ConnectionForm certificate trust', () => {
  const trustCheckbox = () =>
    screen.getByLabelText(/Trust Server Certificate/i) as HTMLInputElement;

  it('does not warn while certificate validation is on', () => {
    render(<ConnectionForm provider="sqlserver" onConnect={() => {}} onBack={() => {}} />);

    expect(trustCheckbox().checked).toBe(false);
    expect(screen.queryByTestId('trust-cert-warning')).toBeNull();
  });

  it('shows a calm note for the default localhost server', () => {
    // A local SQL Server's self-signed certificate is the expected case;
    // shouting "interceptable" at localhost teaches users to ignore the
    // warning that matters on a remote host.
    render(<ConnectionForm provider="sqlserver" onConnect={() => {}} onBack={() => {}} />);

    fireEvent.click(trustCheckbox());

    expect(screen.queryByTestId('trust-cert-warning')).toBeNull();
    const note = screen.getByTestId('trust-cert-note');
    expect(note.textContent).toMatch(/local SQL Server/i);
    expect(note.textContent).toMatch(/self-signed certificate/i);
  });

  it('explains the exposure when opting out of validation for a remote server', () => {
    render(<ConnectionForm provider="sqlserver" onConnect={() => {}} onBack={() => {}} />);

    fireEvent.change(screen.getByPlaceholderText('localhost'), {
      target: { value: 'db.prod.example.com' },
    });
    fireEvent.click(trustCheckbox());

    expect(screen.queryByTestId('trust-cert-note')).toBeNull();
    const warning = screen.getByTestId('trust-cert-warning');
    expect(warning.textContent).toMatch(/disables certificate validation/i);
    expect(warning.textContent).toMatch(/intercept/i);
  });
});

/**
 * "Load databases" used to fail in total silence: a non-ok response fell
 * through with no else branch, a network or JSON error hit a bare `catch {}`,
 * and `finally` stopped the spinner — so the button flickered and the user was
 * left staring at an unchanged form with no idea anything had gone wrong.
 */
describe('ConnectionForm database discovery', () => {
  const loadButton = () => screen.getByTitle('Load databases from server');

  /** Windows auth is the path that actually reaches /api/databases. */
  function renderWithWindowsAuth() {
    render(<ConnectionForm provider="sqlserver" onConnect={() => {}} onBack={() => {}} />);
    fireEvent.click(screen.getByLabelText(/Windows Authentication/i));
  }

  beforeEach(() => vi.unstubAllGlobals());
  afterEach(() => vi.unstubAllGlobals());

  it('reports a non-ok response from the discovery endpoint', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 503 } as Response)));

    renderWithWindowsAuth();
    fireEvent.click(loadButton());

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/could not load databases/i);
  });

  it('reports a network failure from the discovery endpoint', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('Failed to fetch'))));

    renderWithWindowsAuth();
    fireEvent.click(loadButton());

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Failed to fetch');
  });

  it('reports an empty database list rather than leaving the form unchanged', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ databases: [] }) } as Response)),
    );

    renderWithWindowsAuth();
    fireEvent.click(loadButton());

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toMatch(/no databases/i);
  });
});
