import { useState } from 'react';
import {
  ChatHttpError,
  MAX_CONFIRMATION_REASON_LENGTH,
  type ConfirmationRequest,
} from '../lib/chat-api';

export interface ConfirmationCardProps {
  confirmation: ConfirmationRequest;
  /** POSTs the decision; the card clears when `confirmation-resolved` arrives on the stream. */
  onDecide: (approve: boolean, reason?: string) => Promise<void>;
}

/**
 * Proposal card for a parked plan write. The SSE stream sits idle while this
 * card is shown — nothing happens until the user approves, denies, or the
 * server-side timeout denies it. The card is cleared by the stream's own
 * `confirmation-resolved` event (which also fires when the proposal resolves
 * from another tab or by timeout), not by the POST response.
 */
export function ConfirmationCard({ confirmation, onDecide }: ConfirmationCardProps) {
  const [reason, setReason] = useState('');
  const [posting, setPosting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const columns = [...new Set(confirmation.rows.flatMap((row) => Object.keys(row)))];

  const decide = async (approve: boolean) => {
    setPosting(true);
    setError(null);
    try {
      await onDecide(approve, reason.trim() === '' ? undefined : reason.trim());
      // Success: leave the card up (buttons disabled) until the stream's
      // confirmation-resolved event clears it — that event is authoritative.
    } catch (cause) {
      if (cause instanceof ChatHttpError && cause.status === 404) {
        // Already resolved elsewhere (another tab) or timed out server-side.
        // The stream's confirmation-resolved event clears the card.
        setError('This proposal was already resolved (or timed out).');
      } else {
        setError(cause instanceof Error ? cause.message : String(cause));
        setPosting(false); // a transient failure: let the user retry
      }
    }
  };

  return (
    <div className="confirmation-card" role="alertdialog" aria-label="Write proposal">
      <div className="confirmation-title">
        The assistant proposes: <strong>{confirmation.operation}</strong> on{' '}
        <strong>{confirmation.table}</strong>
      </div>
      <div className="confirmation-summary">{confirmation.summary}</div>
      {confirmation.rows.length > 0 && (
        <div className="confirmation-rows">
          <table>
            <thead>
              <tr>
                {columns.map((column) => (
                  <th key={column}>{column}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {confirmation.rows.map((row, index) => (
                <tr key={index}>
                  {columns.map((column) => (
                    <td key={column}>{row[column] === null ? '—' : String(row[column] ?? '')}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div className="confirmation-parked">
        The response is paused — waiting for your approval. Nothing is written until you decide;
        an unanswered proposal denies itself after the server's timeout.
      </div>
      {error && <div className="confirmation-error">{error}</div>}
      <div className="confirmation-actions">
        <input
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          maxLength={MAX_CONFIRMATION_REASON_LENGTH}
          placeholder="Optional reason (used when denying)"
          disabled={posting}
        />
        <button className="approve" onClick={() => void decide(true)} disabled={posting}>
          Approve
        </button>
        <button className="deny" onClick={() => void decide(false)} disabled={posting}>
          Deny
        </button>
      </div>
    </div>
  );
}
