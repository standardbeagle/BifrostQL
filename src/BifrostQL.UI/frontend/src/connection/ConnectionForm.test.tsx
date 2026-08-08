// @vitest-environment jsdom
/**
 * A connect attempt that does not succeed must hand the form back to the user.
 * handleConnect used to set 'connecting' and call onConnect without awaiting
 * it, so nothing ever restored 'idle': a rejected connect, or the routine act
 * of pressing Escape in the credential prompt, left every input and both
 * buttons permanently disabled. The only way out was Back, which discards the
 * host/port/database/SSH details the user had just typed.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
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
