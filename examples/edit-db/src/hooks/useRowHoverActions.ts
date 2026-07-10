import { useCallback, useRef, useState, type PointerEvent, type MutableRefObject } from 'react';

export interface HoveredRow {
    rowId: string;
    el: HTMLElement;
}

export interface UseRowHoverActionsResult {
    hoveredRow: HoveredRow | null;
    hoverRow: (rowId: string, el: HTMLElement) => void;
    scheduleDismiss: () => void;
    cancelDismiss: () => void;
    /** Suppresses the click that follows a long-press so it doesn't also select the row. */
    suppressClick: MutableRefObject<boolean>;
    startPress: (rowId: string, el: HTMLElement, e: PointerEvent) => void;
    onPressMove: () => void;
    cancelPress: () => void;
}

const LONG_PRESS_MS = 500;

/**
 * Row hover/long-press action tracking for the data grid.
 *
 * Mouse: hovering a row opens its action toolbar (with a small dismiss delay so
 * moving onto the toolbar doesn't close it). Touch: a long-press (hold) opens
 * the same actions since hover doesn't exist on touch; a finger move (scroll)
 * cancels the press, and a fired press suppresses the click that follows the
 * lift so it doesn't also select.
 */
export function useRowHoverActions(): UseRowHoverActionsResult {
    const [hoveredRow, setHoveredRow] = useState<HoveredRow | null>(null);
    const dismissTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

    const hoverRow = useCallback((rowId: string, el: HTMLElement) => {
        if (dismissTimer.current) { clearTimeout(dismissTimer.current); dismissTimer.current = null; }
        setHoveredRow({ rowId, el });
    }, []);

    const scheduleDismiss = useCallback(() => {
        if (dismissTimer.current) clearTimeout(dismissTimer.current);
        dismissTimer.current = setTimeout(() => setHoveredRow(null), 150);
    }, []);

    const cancelDismiss = useCallback(() => {
        if (dismissTimer.current) { clearTimeout(dismissTimer.current); dismissTimer.current = null; }
    }, []);

    const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
    const pressMoved = useRef(false);
    const suppressClick = useRef(false);

    const cancelPress = useCallback(() => {
        if (pressTimer.current) { clearTimeout(pressTimer.current); pressTimer.current = null; }
    }, []);

    const startPress = useCallback((rowId: string, el: HTMLElement, e: PointerEvent) => {
        if (e.pointerType !== 'touch') return;
        pressMoved.current = false;
        cancelPress();
        pressTimer.current = setTimeout(() => {
            if (pressMoved.current) return;
            suppressClick.current = true;
            hoverRow(rowId, el);
        }, LONG_PRESS_MS);
    }, [cancelPress, hoverRow]);

    const onPressMove = useCallback(() => {
        pressMoved.current = true;
        cancelPress();
    }, [cancelPress]);

    return {
        hoveredRow,
        hoverRow,
        scheduleDismiss,
        cancelDismiss,
        suppressClick,
        startPress,
        onPressMove,
        cancelPress,
    };
}
