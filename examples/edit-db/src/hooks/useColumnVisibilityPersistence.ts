import { useCallback, useRef, useState } from 'react';
import type { VisibilityState } from '@tanstack/react-table';

const COL_VISIBILITY_STORAGE_PREFIX = 'bifrost-col-visibility:';

function storageKey(tableName: string): string {
    return COL_VISIBILITY_STORAGE_PREFIX + tableName;
}

/**
 * Read a stored selection. Returns null — not `{}` — when there is nothing
 * stored, because "no stored preference" (fall back to the host defaults) and
 * "stored: everything visible" are different states.
 */
export function loadColumnVisibility(tableName: string): VisibilityState | null {
    try {
        const raw = localStorage.getItem(storageKey(tableName));
        if (!raw) return null;
        return sanitizeColumnVisibility(JSON.parse(raw));
    } catch {
        return null;
    }
}

function sanitizeColumnVisibility(value: unknown): VisibilityState | null {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return null;
    }
    const visibility: VisibilityState = {};
    for (const [columnId, visible] of Object.entries(value)) {
        if (typeof visible !== 'boolean') continue;
        visibility[columnId] = visible;
    }
    return visibility;
}

export function saveColumnVisibility(tableName: string, visibility: VisibilityState): void {
    try {
        localStorage.setItem(storageKey(tableName), JSON.stringify(visibility));
    } catch {
        // storage full or unavailable
    }
}

export function clearColumnVisibility(tableName: string): void {
    try {
        localStorage.removeItem(storageKey(tableName));
    } catch {
        // storage unavailable
    }
}

/**
 * Turn a host's list of columns to show into table state: everything named is
 * visible, everything else in `allColumnIds` is hidden. Columns the host names
 * that this table doesn't have are ignored, so one config can cover a schema
 * that varies between deployments.
 */
export function visibilityFromDefaults(allColumnIds: string[], defaultVisible?: string[]): VisibilityState {
    if (!defaultVisible || defaultVisible.length === 0) return {};
    const wanted = new Set(defaultVisible);
    const visibility: VisibilityState = {};
    for (const id of allColumnIds) {
        visibility[id] = wanted.has(id);
    }
    return visibility;
}

export interface UseColumnVisibilityPersistenceResult {
    columnVisibility: VisibilityState;
    setColumnVisibility: React.Dispatch<React.SetStateAction<VisibilityState>>;
    /** Drop the stored selection and go back to the host defaults for this table. */
    resetColumnVisibility: () => void;
}

/**
 * Owns which columns a table shows, persisted per table to localStorage.
 *
 * A viewer picking columns is a durable preference, not a per-visit one: the
 * selection survives reload and return visits. Host defaults apply until the
 * viewer makes a choice, and `resetColumnVisibility` puts them back.
 *
 * Only a change through the returned setter is written. Merely opening a table
 * must not store its defaults — that would freeze today's defaults into every
 * viewer's storage and make a later change to them invisible.
 */
export function useColumnVisibilityPersistence(
    tableName: string | undefined,
    defaultVisibility: VisibilityState,
): UseColumnVisibilityPersistenceResult {
    const [columnVisibility, setState] = useState<VisibilityState>(
        () => (tableName ? loadColumnVisibility(tableName) : null) ?? defaultVisibility,
    );

    // Mirrors state so the setter can resolve a functional updater without
    // depending on the current value (and so re-creating on every change).
    const currentRef = useRef(columnVisibility);

    const apply = useCallback((next: VisibilityState) => {
        currentRef.current = next;
        setState(next);
    }, []);

    const setColumnVisibility = useCallback<React.Dispatch<React.SetStateAction<VisibilityState>>>(
        (updater) => {
            const next = typeof updater === 'function' ? updater(currentRef.current) : updater;
            apply(next);
            if (tableName) saveColumnVisibility(tableName, next);
        },
        [apply, tableName],
    );

    const resetColumnVisibility = useCallback(() => {
        if (tableName) clearColumnVisibility(tableName);
        apply(defaultVisibility);
    }, [apply, defaultVisibility, tableName]);

    // Re-read on table switch. Done during render (not in an effect) so the grid
    // never paints one table's column selection over another's rows.
    const tableNameRef = useRef(tableName);
    if (tableName && tableName !== tableNameRef.current) {
        tableNameRef.current = tableName;
        apply(loadColumnVisibility(tableName) ?? defaultVisibility);
    }

    // The defaults only bind while nothing is stored — a host that changes them
    // must not overwrite a selection the viewer already made.
    const defaultsRef = useRef(defaultVisibility);
    if (tableName && defaultsRef.current !== defaultVisibility) {
        defaultsRef.current = defaultVisibility;
        if (loadColumnVisibility(tableName) === null) {
            apply(defaultVisibility);
        }
    }

    return { columnVisibility, setColumnVisibility, resetColumnVisibility };
}
