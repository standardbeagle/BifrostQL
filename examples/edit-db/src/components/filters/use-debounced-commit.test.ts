import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useDebouncedCommit } from './use-debounced-commit';

const DELAY = 300;

describe('useDebouncedCommit', () => {
    beforeEach(() => {
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('runs the scheduled commit after the delay', () => {
        const { result } = renderHook(() => useDebouncedCommit(DELAY));
        const commit = vi.fn();

        result.current.schedule(commit);
        expect(commit).not.toHaveBeenCalled();

        vi.advanceTimersByTime(DELAY);
        expect(commit).toHaveBeenCalledTimes(1);
    });

    it('replaces a pending commit when scheduled again', () => {
        const { result } = renderHook(() => useDebouncedCommit(DELAY));
        const first = vi.fn();
        const second = vi.fn();

        result.current.schedule(first);
        vi.advanceTimersByTime(DELAY - 1);
        result.current.schedule(second);
        vi.advanceTimersByTime(DELAY);

        expect(first).not.toHaveBeenCalled();
        expect(second).toHaveBeenCalledTimes(1);
    });

    it('cancel drops the pending commit without running it', () => {
        const { result } = renderHook(() => useDebouncedCommit(DELAY));
        const commit = vi.fn();

        result.current.schedule(commit);
        result.current.cancel();
        vi.advanceTimersByTime(DELAY);

        expect(commit).not.toHaveBeenCalled();
    });

    it('flush runs the pending commit immediately, exactly once', () => {
        const { result } = renderHook(() => useDebouncedCommit(DELAY));
        const commit = vi.fn();

        result.current.schedule(commit);
        result.current.flush();
        expect(commit).toHaveBeenCalledTimes(1);

        // The original timer must not fire the commit a second time.
        vi.advanceTimersByTime(DELAY);
        expect(commit).toHaveBeenCalledTimes(1);
    });

    it('flush is a no-op when nothing is pending', () => {
        const { result } = renderHook(() => useDebouncedCommit(DELAY));
        expect(() => result.current.flush()).not.toThrow();
    });

    it('flushes the pending commit on unmount instead of dropping it', () => {
        const { result, unmount } = renderHook(() => useDebouncedCommit(DELAY));
        const commit = vi.fn();

        result.current.schedule(commit);
        unmount();

        expect(commit).toHaveBeenCalledTimes(1);
    });

    it('does not run a cancelled commit on unmount', () => {
        const { result, unmount } = renderHook(() => useDebouncedCommit(DELAY));
        const commit = vi.fn();

        result.current.schedule(commit);
        result.current.cancel();
        unmount();

        expect(commit).not.toHaveBeenCalled();
    });
});
