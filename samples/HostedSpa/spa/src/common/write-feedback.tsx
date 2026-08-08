import { useCallback, useState } from 'react';

/**
 * The outcome of the most recent write plus the runner that produces it.
 *
 * One instance per screen (or per independent form on a screen), obtained from
 * {@link useWriteFeedback} and rendered by {@link WriteFeedbackRegion}.
 */
export interface WriteFeedback {
  /**
   * Message of the most recent *failed* write, or `null` when the last write
   * succeeded, was cleared, or none has run. Carries the server's own message
   * verbatim — nothing is invented or softened.
   */
  error: string | null;
  /** Confirmation of the most recent *successful* write, or `null`. */
  notice: string | null;
  /**
   * Run one write and report its outcome.
   *
   * Resolves `true` only when the write actually succeeded. Callers gate every
   * post-write effect on that value — clearing the form, showing a derived
   * status, issuing a follow-on mutation — so a rejected write can never leave
   * the screen claiming work that did not happen.
   *
   * @param write - Invokes the mutation; must reject on failure (a TanStack
   *   `mutateAsync` does).
   * @param successMessage - Confirmation shown when the write succeeds.
   */
  run: (
    write: () => Promise<unknown>,
    successMessage: string,
  ) => Promise<boolean>;
  /** Drop the current message, e.g. when the operator starts editing again. */
  clear: () => void;
}

/**
 * Single feedback seam for every write in this SPA.
 *
 * Bifrost mutations reject on a transport failure, an HTTP error, or a GraphQL
 * `errors` payload — which is how a policy or tenant denial arrives. A
 * fire-and-forget `mutate()` swallows all of those: the screen resets the form
 * and looks like it worked. Routing writes through {@link WriteFeedback.run}
 * makes the rejection observable at the call site (the resolved boolean) and
 * visible to the operator (the message), with the operator's entered data left
 * untouched so the write can be retried.
 *
 * This is a sample app — its patterns get copied into real deployments — so a
 * silent write is not a cosmetic flaw here; it propagates.
 */
export function useWriteFeedback(): WriteFeedback {
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const run = useCallback(
    async (write: () => Promise<unknown>, successMessage: string) => {
      setError(null);
      setNotice(null);
      try {
        await write();
        setNotice(successMessage);
        return true;
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : String(cause));
        return false;
      }
    },
    [],
  );

  const clear = useCallback(() => {
    setError(null);
    setNotice(null);
  }, []);

  return { error, notice, run, clear };
}

/** Props for {@link WriteFeedbackRegion}. */
export interface WriteFeedbackRegionProps {
  /** The screen's feedback state, from {@link useWriteFeedback}. */
  feedback: WriteFeedback;
  /**
   * Test-id prefix; the failure paragraph is `<testId>-error` and the success
   * paragraph `<testId>-notice`.
   */
  testId: string;
}

/**
 * Renders the current write outcome: a failure as an assertive `role="alert"`,
 * a success as a polite `role="status"`. Renders nothing when no write has run.
 */
export function WriteFeedbackRegion({
  feedback,
  testId,
}: WriteFeedbackRegionProps) {
  if (feedback.error !== null) {
    return (
      <p role="alert" data-testid={`${testId}-error`}>
        {feedback.error}
      </p>
    );
  }
  if (feedback.notice !== null) {
    return (
      <p role="status" data-testid={`${testId}-notice`}>
        {feedback.notice}
      </p>
    );
  }
  return null;
}
