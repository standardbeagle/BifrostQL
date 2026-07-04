import { useCallback, useMemo, useState } from 'react';
import type {
  VirtualScrollConfig,
  VirtualScrollState,
  VisibleRange,
} from '../use-bifrost-table.types';

export interface UseVirtualScrollOptions {
  config: VirtualScrollConfig | undefined;
  data: Record<string, unknown>[];
}

/**
 * Owns virtual-scroll state and derives the visible (windowed) row slice,
 * total height, and scroll navigation helpers.
 */
export function useVirtualScroll({
  config,
  data,
}: UseVirtualScrollOptions): VirtualScrollState {
  const vsEnabled = config?.enabled ?? false;
  const vsRowHeight = config?.rowHeight ?? 35;
  const vsContainerHeight = config?.containerHeight ?? 400;
  const vsOverscan = config?.overscan ?? 5;

  const [vsScrollTop, setVsScrollTop] = useState(0);

  const vsTotalHeight = useMemo(
    () => data.length * vsRowHeight,
    [data.length, vsRowHeight],
  );

  const vsVisibleRange = useMemo((): VisibleRange => {
    if (!vsEnabled) {
      return {
        startIndex: 0,
        endIndex: data.length - 1,
        overscanStartIndex: 0,
        overscanEndIndex: data.length - 1,
      };
    }
    const totalRows = data.length;
    if (totalRows === 0) {
      return {
        startIndex: 0,
        endIndex: -1,
        overscanStartIndex: 0,
        overscanEndIndex: -1,
      };
    }
    const startIndex = Math.floor(vsScrollTop / vsRowHeight);
    const visibleCount = Math.ceil(vsContainerHeight / vsRowHeight);
    const endIndex = Math.min(startIndex + visibleCount - 1, totalRows - 1);
    const overscanStartIndex = Math.max(0, startIndex - vsOverscan);
    const overscanEndIndex = Math.min(totalRows - 1, endIndex + vsOverscan);
    return { startIndex, endIndex, overscanStartIndex, overscanEndIndex };
  }, [
    vsEnabled,
    vsScrollTop,
    vsRowHeight,
    vsContainerHeight,
    vsOverscan,
    data.length,
  ]);

  const vsOffsetTop = useMemo(
    () => vsVisibleRange.overscanStartIndex * vsRowHeight,
    [vsVisibleRange.overscanStartIndex, vsRowHeight],
  );

  const vsVisibleRows = useMemo(() => {
    if (!vsEnabled) return data;
    const { overscanStartIndex, overscanEndIndex } = vsVisibleRange;
    if (overscanEndIndex < 0) return [];
    return data.slice(overscanStartIndex, overscanEndIndex + 1);
  }, [vsEnabled, vsVisibleRange, data]);

  const vsOnScroll = useCallback((scrollTop: number) => {
    const clamped = Math.max(0, scrollTop);
    setVsScrollTop(clamped);
  }, []);

  const vsScrollToRow = useCallback(
    (index: number) => {
      const clamped = Math.max(0, Math.min(index, data.length - 1));
      const targetScrollTop = clamped * vsRowHeight;
      setVsScrollTop(targetScrollTop);
    },
    [data.length, vsRowHeight],
  );

  const vsScrollToTop = useCallback(() => {
    setVsScrollTop(0);
  }, []);

  const vsScrollToBottom = useCallback(() => {
    const maxScroll = Math.max(0, vsTotalHeight - vsContainerHeight);
    setVsScrollTop(maxScroll);
  }, [vsTotalHeight, vsContainerHeight]);

  return {
    enabled: vsEnabled,
    visibleRange: vsVisibleRange,
    totalHeight: vsTotalHeight,
    offsetTop: vsOffsetTop,
    visibleRows: vsVisibleRows,
    scrollToRow: vsScrollToRow,
    scrollToTop: vsScrollToTop,
    scrollToBottom: vsScrollToBottom,
    onScroll: vsOnScroll,
    scrollTop: vsScrollTop,
    containerHeight: vsContainerHeight,
    rowHeight: vsRowHeight,
    isVirtualized: vsEnabled && data.length > 0,
  };
}
