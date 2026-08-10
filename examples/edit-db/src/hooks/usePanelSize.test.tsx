// @vitest-environment jsdom
/**
 * Persistent panel resizing. The size must clamp to [min, max] (a panel
 * dragged to zero is unrecoverable — its handle goes with it), persist per
 * key across mounts, and be adjustable by keyboard: a drag-only separator is
 * unreachable to keyboard users.
 */
import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { usePanelSize } from './usePanelSize';

const OPTS = { key: 'test-panel', initial: 240, min: 150, max: 560, axis: 'x' as const };

function keyEvent(key: string) {
    return { key, preventDefault: () => { /* noop */ } } as unknown as React.KeyboardEvent<HTMLElement>;
}

describe('usePanelSize', () => {
    beforeEach(() => localStorage.clear());

    it('starts at the initial size with nothing persisted', () => {
        const { result } = renderHook(() => usePanelSize(OPTS));
        expect(result.current.size).toBe(240);
    });

    it('restores a persisted size on mount, clamped to bounds', () => {
        localStorage.setItem('edit-db:panel:test-panel', '9999');
        const { result } = renderHook(() => usePanelSize(OPTS));
        expect(result.current.size).toBe(560);
    });

    it('ignores a corrupt persisted value', () => {
        localStorage.setItem('edit-db:panel:test-panel', 'garbage');
        const { result } = renderHook(() => usePanelSize(OPTS));
        expect(result.current.size).toBe(240);
    });

    it('resizes with arrow keys and persists the result', () => {
        const { result } = renderHook(() => usePanelSize(OPTS));
        act(() => result.current.onKeyDown(keyEvent('ArrowRight')));
        expect(result.current.size).toBe(256);
        expect(localStorage.getItem('edit-db:panel:test-panel')).toBe('256');
        act(() => result.current.onKeyDown(keyEvent('ArrowLeft')));
        expect(result.current.size).toBe(240);
    });

    it('clamps keyboard resizing at the minimum', () => {
        localStorage.setItem('edit-db:panel:test-panel', '155');
        const { result } = renderHook(() => usePanelSize(OPTS));
        act(() => result.current.onKeyDown(keyEvent('ArrowLeft')));
        expect(result.current.size).toBe(150);
    });

    it('inverts keyboard direction for handles that precede their panel', () => {
        const { result } = renderHook(() =>
            usePanelSize({ key: 'inv', initial: 300, min: 120, max: 800, axis: 'y', invert: true }));
        // Handle above a bottom panel: ArrowUp grows it.
        act(() => result.current.onKeyDown(keyEvent('ArrowUp')));
        expect(result.current.size).toBe(316);
        act(() => result.current.onKeyDown(keyEvent('ArrowDown')));
        expect(result.current.size).toBe(300);
    });
});
