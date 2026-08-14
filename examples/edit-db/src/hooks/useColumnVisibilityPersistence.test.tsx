import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import {
    useColumnVisibilityPersistence,
    visibilityFromDefaults,
    loadColumnVisibility,
} from './useColumnVisibilityPersistence';

describe('visibilityFromDefaults', () => {
    it('shows the named columns and hides the rest', () => {
        expect(visibilityFromDefaults(['id', 'name', 'created'], ['id', 'name'])).toEqual({
            id: true,
            name: true,
            created: false,
        });
    });

    it('ignores names the table does not have', () => {
        expect(visibilityFromDefaults(['id'], ['id', 'absent'])).toEqual({ id: true });
    });

    it('leaves every column visible when the host names none', () => {
        expect(visibilityFromDefaults(['id', 'name'], undefined)).toEqual({});
        expect(visibilityFromDefaults(['id', 'name'], [])).toEqual({});
    });
});

describe('useColumnVisibilityPersistence', () => {
    beforeEach(() => {
        localStorage.clear();
    });

    const defaults = { id: true, created: false };

    it('starts from the host defaults when nothing is stored', () => {
        const { result } = renderHook(() => useColumnVisibilityPersistence('workshops', defaults));
        expect(result.current.columnVisibility).toEqual(defaults);
    });

    it('stores nothing until the viewer changes something', () => {
        const { rerender } = renderHook(
            ({ table }) => useColumnVisibilityPersistence(table, defaults),
            { initialProps: { table: 'workshops' } },
        );
        rerender({ table: 'coaches' });
        rerender({ table: 'workshops' });

        expect(loadColumnVisibility('workshops')).toBeNull();
        expect(loadColumnVisibility('coaches')).toBeNull();
    });

    it('persists a selection and restores it on remount', () => {
        const first = renderHook(() => useColumnVisibilityPersistence('workshops', defaults));
        act(() => first.result.current.setColumnVisibility({ id: true, created: true }));
        expect(loadColumnVisibility('workshops')).toEqual({ id: true, created: true });

        const second = renderHook(() => useColumnVisibilityPersistence('workshops', defaults));
        expect(second.result.current.columnVisibility).toEqual({ id: true, created: true });
    });

    it('keeps each table on its own selection', () => {
        const { result, rerender } = renderHook(
            ({ table }) => useColumnVisibilityPersistence(table, defaults),
            { initialProps: { table: 'workshops' } },
        );
        act(() => result.current.setColumnVisibility({ id: false, created: true }));

        rerender({ table: 'coaches' });
        expect(result.current.columnVisibility).toEqual(defaults);

        rerender({ table: 'workshops' });
        expect(result.current.columnVisibility).toEqual({ id: false, created: true });
    });

    it('returns to the defaults on reset and forgets the stored selection', () => {
        const { result } = renderHook(() => useColumnVisibilityPersistence('workshops', defaults));
        act(() => result.current.setColumnVisibility({ id: false, created: true }));

        act(() => result.current.resetColumnVisibility());

        expect(result.current.columnVisibility).toEqual(defaults);
        expect(loadColumnVisibility('workshops')).toBeNull();
    });

    it('does not let changed host defaults overwrite a stored selection', () => {
        const stored = { id: false, created: true };
        const { result, rerender } = renderHook(
            ({ d }) => useColumnVisibilityPersistence('workshops', d),
            { initialProps: { d: defaults as Record<string, boolean> } },
        );
        act(() => result.current.setColumnVisibility(stored));

        rerender({ d: { id: true, created: true, extra: false } });

        expect(result.current.columnVisibility).toEqual(stored);
    });

    it('applies new host defaults while nothing is stored', () => {
        const { result, rerender } = renderHook(
            ({ d }) => useColumnVisibilityPersistence('workshops', d),
            { initialProps: { d: {} as Record<string, boolean> } },
        );

        rerender({ d: { id: true, created: false } });

        expect(result.current.columnVisibility).toEqual({ id: true, created: false });
    });
});
