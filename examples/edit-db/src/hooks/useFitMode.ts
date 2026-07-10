import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { TABLE_HEADER_HEIGHT, TABLE_ROW_HEIGHT } from '@/components/ui/table';

/** Sentinel page-size value meaning "fit as many rows as the viewport allows". */
export const FIT_SENTINEL = -1;

export interface UseFitModeResult {
    fitMode: boolean;
    setFitMode: React.Dispatch<React.SetStateAction<boolean>>;
    /** Rows that fit the current scroll container height. */
    computeFitSize: () => number;
}

/**
 * "Fit" page-sizing: derive the page size from the scroll container's height so
 * the grid shows exactly as many rows as fit, re-fitting when the container
 * resizes. The resize path is debounced so dragging a splitter/window doesn't
 * fire a burst of queries (each distinct page size is a new query key) — only
 * the settled size runs.
 */
export function useFitMode(
    scrollRef: RefObject<HTMLDivElement>,
    onPageSizeChange: (pageSize: number) => void,
): UseFitModeResult {
    const [fitMode, setFitMode] = useState(true);
    const lastFitSize = useRef(0);

    const computeFitSize = useCallback(() => {
        const el = scrollRef.current;
        if (!el) return 10;
        return Math.max(5, Math.floor((el.clientHeight - TABLE_HEADER_HEIGHT) / TABLE_ROW_HEIGHT));
    }, [scrollRef]);

    useEffect(() => {
        if (!fitMode) return;
        const el = scrollRef.current;
        if (!el) return;
        const apply = () => {
            const size = computeFitSize();
            if (size === lastFitSize.current) return;
            lastFitSize.current = size;
            onPageSizeChange(size);
        };
        const raf = requestAnimationFrame(apply);
        let debounce: ReturnType<typeof setTimeout> | null = null;
        const ro = new ResizeObserver(() => {
            if (debounce) clearTimeout(debounce);
            debounce = setTimeout(() => requestAnimationFrame(apply), 200);
        });
        ro.observe(el);
        return () => {
            cancelAnimationFrame(raf);
            if (debounce) clearTimeout(debounce);
            ro.disconnect();
        };
    }, [fitMode, computeFitSize, onPageSizeChange, scrollRef]);

    return { fitMode, setFitMode, computeFitSize };
}
