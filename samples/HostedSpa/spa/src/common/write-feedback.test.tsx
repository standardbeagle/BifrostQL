import { useState } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useWriteFeedback, WriteFeedbackRegion } from './write-feedback';

/**
 * Minimal host for the seam: a button that runs `write` through
 * {@link useWriteFeedback} and clears its "form" only when the run reports
 * success — the exact gating every screen in this SPA needs.
 */
function Harness({ write }: { write: () => Promise<unknown> }) {
  const feedback = useWriteFeedback();
  const [cleared, setCleared] = useState(false);

  return (
    <div>
      <WriteFeedbackRegion feedback={feedback} testId="harness" />
      <span data-testid="harness-cleared">{cleared ? 'yes' : 'no'}</span>
      <button
        type="button"
        onClick={() => {
          void feedback.run(write, 'Saved.').then((ok) => {
            if (ok) {
              setCleared(true);
            }
          });
        }}
      >
        Save
      </button>
      <button type="button" onClick={() => feedback.clear()}>
        Clear
      </button>
    </div>
  );
}

describe('useWriteFeedback', () => {
  it('reports a rejected write as an alert and resolves false', async () => {
    // Arrange: a write that the server rejects.
    const user = userEvent.setup();
    let outcome: boolean | null = null;
    function Capture() {
      const feedback = useWriteFeedback();
      return (
        <div>
          <WriteFeedbackRegion feedback={feedback} testId="capture" />
          <button
            type="button"
            onClick={() => {
              void feedback
                .run(
                  () => Promise.reject(new Error('access denied')),
                  'Saved.',
                )
                .then((ok) => {
                  outcome = ok;
                });
            }}
          >
            Save
          </button>
        </div>
      );
    }
    render(<Capture />);

    // Act
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert: the failure is visible and the run reported failure.
    await waitFor(() =>
      expect(screen.getByTestId('capture-error')).toHaveTextContent(
        'access denied',
      ),
    );
    expect(screen.getByTestId('capture-error')).toHaveAttribute(
      'role',
      'alert',
    );
    expect(outcome).toBe(false);
  });

  it('reports a successful write as a status notice', async () => {
    // Arrange
    const user = userEvent.setup();
    render(<Harness write={() => Promise.resolve('ok')} />);

    // Act
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('harness-notice')).toHaveTextContent('Saved.'),
    );
    expect(screen.getByTestId('harness-cleared')).toHaveTextContent('yes');
    expect(screen.queryByTestId('harness-error')).not.toBeInTheDocument();
  });

  it('clears a previous failure when the next write succeeds', async () => {
    // Arrange: first attempt fails, second succeeds.
    const user = userEvent.setup();
    let attempt = 0;
    const write = () => {
      attempt += 1;
      return attempt === 1
        ? Promise.reject(new Error('boom'))
        : Promise.resolve('ok');
    };
    render(<Harness write={write} />);

    // Act
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(screen.getByTestId('harness-error')).toBeInTheDocument(),
    );
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('harness-notice')).toBeInTheDocument(),
    );
    expect(screen.queryByTestId('harness-error')).not.toBeInTheDocument();
  });

  it('clear() removes the current message', async () => {
    // Arrange
    const user = userEvent.setup();
    render(<Harness write={() => Promise.reject(new Error('boom'))} />);
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(screen.getByTestId('harness-error')).toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByRole('button', { name: 'Clear' }));

    // Assert
    expect(screen.queryByTestId('harness-error')).not.toBeInTheDocument();
  });

  it('reports a non-Error rejection without inventing a message', async () => {
    // Arrange: a rejection that is not an Error instance.
    const user = userEvent.setup();
    render(<Harness write={() => Promise.reject('plain string failure')} />);

    // Act
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() =>
      expect(screen.getByTestId('harness-error')).toHaveTextContent(
        'plain string failure',
      ),
    );
  });
});
