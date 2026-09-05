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

/**
 * M26b follow-up: with peer auth the form hardcoded `psqlUser: 'postgres'`,
 * so on any host not logged in as `postgres` the listing was refused. The form
 * must default to the host's current user (always permitted by the gate) and
 * only offer accounts the host reports as permitted.
 */
describe('ConnectionForm peer auth OS user', () => {
  const loadButton = () => screen.getByTitle('Load databases from server');

  beforeEach(() => vi.unstubAllGlobals());
  afterEach(() => vi.unstubAllGlobals());

  const peerUsersPayload = { current: 'alice', permitted: ['alice', 'postgres'] };

  function stubPeerFetch(databasesResponse: Partial<Response>) {
    const posts: { url: string; body: string }[] = [];
    vi.stubGlobal('fetch', vi.fn((input: unknown, init?: { body?: string }) => {
      const url = String(input);
      if (url.includes('/api/databases/peer-users')) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(peerUsersPayload) } as Response);
      }
      posts.push({ url, body: init?.body ?? '' });
      return Promise.resolve(databasesResponse as Response);
    }));
    return posts;
  }

  async function renderPeerForm(posts: unknown) {
    render(<ConnectionForm provider="postgres" onConnect={() => {}} onBack={() => {}} />);
    fireEvent.click(screen.getByLabelText(/Peer \/ Ident/i));
    // Wait for the permitted-user choices before interacting, so the form has
    // settled on its default OS user.
    await screen.findByLabelText(/OS user/i);
    fireEvent.click(loadButton());
    return posts as { url: string; body: string }[];
  }

  it('submits the host current user, not a hardcoded postgres', async () => {
    const posts = stubPeerFetch({ ok: true, json: () => Promise.resolve({ databases: ['appdb'] }) });

    await renderPeerForm(posts);

    await waitFor(() => expect(posts.some((p) => p.url === '/api/databases')).toBe(true));
    const payload = JSON.parse(posts.find((p) => p.url === '/api/databases')!.body);
    expect(payload.peerAuth).toBe(true);
    expect(payload.psqlUser).toBe('alice');
  });

  it('never submits the hardcoded postgres, even before the permitted set has loaded', async () => {
    // Negative half of the revert-proof: the pre-fix form sent 'postgres' on
    // every peer-auth load. Click Load without waiting for the OS-user select,
    // so the payload assertion itself is what goes RED against the old code
    // (the fact above fails earlier, on the missing select).
    const posts = stubPeerFetch({ ok: true, json: () => Promise.resolve({ databases: ['appdb'] }) });
    render(<ConnectionForm provider="postgres" onConnect={() => {}} onBack={() => {}} />);
    fireEvent.click(screen.getByLabelText(/Peer \/ Ident/i));
    fireEvent.click(loadButton());

    await waitFor(() => expect(posts.some((p) => p.url === '/api/databases')).toBe(true));
    const payload = JSON.parse(posts.find((p) => p.url === '/api/databases')!.body);
    expect(payload.peerAuth).toBe(true);
    // null (discovery still in flight) and 'alice' are both gate-permitted;
    // 'postgres' is the value the gate refuses on every host not logged in as it.
    expect(payload.psqlUser).not.toBe('postgres');
  });

  it('renders a refusal as a validation message naming the permitted accounts', async () => {
    const posts = stubPeerFetch({
      ok: false,
      status: 400,
      json: () => Promise.resolve({
        error: 'The requested psql OS user is not permitted. Permitted accounts for peer auth: alice.',
      }),
    });

    await renderPeerForm(posts);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Permitted accounts for peer auth: alice');
    expect(alert.textContent).not.toMatch(/server returned 400/);
  });
});
