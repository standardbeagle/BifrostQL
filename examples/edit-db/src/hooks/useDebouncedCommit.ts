import { useEffect, useMemo, useRef } from 'react';

export interface DebouncedCommit {
    /** Debounce `commit`: it replaces any pending commit and runs after the delay. */
    schedule: (commit: () => void) => void;
    /** Drop the pending commit (if any) without running it. */
    cancel: () => void;
    /** Run the pending commit (if any) immediately instead of waiting out the delay. */
    flush: () => void;
}

/**
 * Debounce a commit callback with a pending-flush guarantee: the last scheduled
 * commit either runs after `delayMs`, runs early via `flush()`, or is dropped
 * via `cancel()` — and a pending commit is flushed automatically on unmount, so
 * a value entered just before the host unmounts (e.g. Radix closing a dropdown
 * that contains the filter input) isn't silently lost.
 *
 * Shared by the column-filter widgets so debounce/flush bookkeeping isn't
 * re-implemented per filter type.
 */
export function useDebouncedCommit(delayMs: number): DebouncedCommit {
    const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const pendingRef = useRef<(() => void) | null>(null);
    const delayRef = useRef(delayMs);
    delayRef.current = delayMs;

    const api = useMemo<DebouncedCommit>(() => {
        const clearTimer = () => {
            if (timerRef.current) {
                clearTimeout(timerRef.current);
                timerRef.current = null;
            }
        };
        const flush = () => {
            clearTimer();
            const pending = pendingRef.current;
            pendingRef.current = null;
            pending?.();
        };
        return {
            schedule: (commit) => {
                clearTimer();
                pendingRef.current = commit;
                timerRef.current = setTimeout(flush, delayRef.current);
            },
            cancel: () => {
                clearTimer();
                pendingRef.current = null;
            },
            flush,
        };
    }, []);

    // Flush — not drop — whatever is still pending when the host unmounts.
    useEffect(() => () => api.flush(), [api]);

    return api;
}
