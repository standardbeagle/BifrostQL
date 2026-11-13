import { useEffect, useRef, useState } from 'react';
import type { ColumnSizingState } from '@tanstack/react-table';

export const COL_MIN_WIDTH = 60;
export const COL_MAX_AUTO_WIDTH = 450;
export const COL_DEFAULT_WIDTH = 150;
const COL_SIZING_STORAGE_PREFIX = 'bifrost-col-sizes:';

function loadColumnSizing(tableName: string): ColumnSizingState {
    try {
        const raw = localStorage.getItem(COL_SIZING_STORAGE_PREFIX + tableName);
        if (!raw) return {};
        return sanitizeColumnSizing(JSON.parse(raw));
    } catch {
        return {};
    }
}

function sanitizeColumnSizing(value: unknown): ColumnSizingState {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return {};
    }

    const sizing: ColumnSizingState = {};
    for (const [columnId, width] of Object.entries(value)) {
        if (typeof width !== 'number' || !Number.isFinite(width)) continue;
        sizing[columnId] = Math.min(Math.max(width, COL_MIN_WIDTH), COL_MAX_AUTO_WIDTH);
    }
    return sizing;
}

function saveColumnSizing(tableName: string, sizing: ColumnSizingState): void {
    try {
        if (Object.keys(sizing).length === 0) {
            localStorage.removeItem(COL_SIZING_STORAGE_PREFIX + tableName);
        } else {
            localStorage.setItem(COL_SIZING_STORAGE_PREFIX + tableName, JSON.stringify(sizing));
        }
    } catch {
        // storage full or unavailable
    }
}

export interface UseColumnSizingPersistenceResult {
    columnSizing: ColumnSizingState;
    setColumnSizing: React.Dispatch<React.SetStateAction<ColumnSizingState>>;
}

/**
 * Owns per-table column sizing, persisted to localStorage.
 *
 * Writes are debounced so a live ('onChange') resize drag doesn't write on every
 * mousemove. `loaded` remembers the sizing object last read from storage, and
 * the effect skips persisting while the state is still that very object — it
 * would only write back what storage already holds (on mount and after a
 * table switch). `pendingSizingRef` carries the latest unsaved state so a
 * table switch or unmount flushes it instead of dropping a resize made less
 * than the debounce window ago.
 */
export function useColumnSizingPersistence(tableName?: string): UseColumnSizingPersistenceResult {
    const [loaded, setLoaded] = useState(() => ({
        tableName,
        sizing: tableName ? loadColumnSizing(tableName) : {},
    }));
    const [columnSizing, setColumnSizing] = useState<ColumnSizingState>(loaded.sizing);

    const pendingSizingRef = useRef<{ tableName: string; sizing: ColumnSizingState } | null>(null);

    useEffect(() => {
        // A pending write for a previous table can no longer be superseded — flush it.
        const stale = pendingSizingRef.current;
        if (stale && stale.tableName !== tableName) {
            pendingSizingRef.current = null;
            saveColumnSizing(stale.tableName, stale.sizing);
        }
        if (!tableName) return;
        if (columnSizing === loaded.sizing) return;
        pendingSizingRef.current = { tableName, sizing: columnSizing };
        const t = setTimeout(() => {
            pendingSizingRef.current = null;
            saveColumnSizing(tableName, columnSizing);
        }, 300);
        return () => clearTimeout(t);
    }, [tableName, columnSizing, loaded.sizing]);

    // Flush any still-pending sizing write on unmount rather than dropping it.
    useEffect(() => () => {
        const pending = pendingSizingRef.current;
        if (pending) saveColumnSizing(pending.tableName, pending.sizing);
    }, []);

    // Reset persisted sizing when table changes
    if (tableName && tableName !== loaded.tableName) {
        const restored = loadColumnSizing(tableName);
        setLoaded({ tableName, sizing: restored });
        setColumnSizing(restored);
    }

    return { columnSizing, setColumnSizing };
}
