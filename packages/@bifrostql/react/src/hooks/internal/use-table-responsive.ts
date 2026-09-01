import { useEffect, useMemo, useRef, useState } from 'react';
import {
  DEFAULT_BREAKPOINTS,
  getBreakpointFromWidth,
  getColumnsForBreakpoint,
} from '../../utils/table-breakpoints';
import { canAccessWindow } from '../../utils/dom-env';
import type {
  BreakpointConfig,
  CardViewRow,
  ColumnConfig,
  ResponsiveColumnConfig,
  ResponsiveState,
} from '../use-bifrost-table.types';

export interface UseTableResponsiveOptions<T> {
  breakpointsProp: Partial<BreakpointConfig> | undefined;
  responsiveColumns: ResponsiveColumnConfig[] | undefined;
  visibleColumns: string[];
  columns: ColumnConfig[];
  rowKey: string;
  data: T[];
}

/**
 * Owns viewport-width tracking and derives the active breakpoint, responsive
 * column visibility, and card-view projection of the data.
 */
export function useTableResponsive<T = Record<string, unknown>>({
  breakpointsProp,
  responsiveColumns,
  visibleColumns,
  columns,
  rowKey,
  data,
}: UseTableResponsiveOptions<T>): ResponsiveState<T> {
  // Memoize on the numeric thresholds, not the prop object's identity: call
  // sites pass an inline literal, so identity-keyed memoization rebuilt this
  // object — and everything downstream of it, down to `cardViewData` — on
  // every single render.
  const resolvedBreakpoints = useMemo(
    () => ({ ...DEFAULT_BREAKPOINTS, ...breakpointsProp }),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed on the numeric thresholds, deliberately not the prop object's identity
    [
      breakpointsProp?.xs,
      breakpointsProp?.sm,
      breakpointsProp?.md,
      breakpointsProp?.lg,
      breakpointsProp?.xl,
    ],
  );

  const [windowWidth, setWindowWidth] = useState<number>(() =>
    canAccessWindow() ? window.innerWidth : 1024,
  );

  const breakpointsRef = useRef(resolvedBreakpoints);
  breakpointsRef.current = resolvedBreakpoints;

  useEffect(() => {
    if (!canAccessWindow()) return;

    let frame: number | null = null;

    const handleResize = () => {
      // A drag fires resize dozens of times per frame. Coalesce to one update
      // per animation frame, and then only when the width actually crosses
      // into a different breakpoint bucket — every pixel of an in-bucket
      // resize would otherwise rebuild the O(rows x cols) card projection.
      if (frame !== null) return;
      frame = requestAnimationFrame(() => {
        frame = null;
        const width = window.innerWidth;
        setWindowWidth((previous) => {
          const breakpoints = breakpointsRef.current;
          return getBreakpointFromWidth(previous, breakpoints) ===
            getBreakpointFromWidth(width, breakpoints)
            ? previous
            : width;
        });
      });
    };

    window.addEventListener('resize', handleResize);
    return () => {
      if (frame !== null) cancelAnimationFrame(frame);
      window.removeEventListener('resize', handleResize);
    };
  }, []);

  const currentBreakpoint = useMemo(
    () => getBreakpointFromWidth(windowWidth, resolvedBreakpoints),
    [windowWidth, resolvedBreakpoints],
  );

  const isMobile = currentBreakpoint === 'xs' || currentBreakpoint === 'sm';
  const isTablet = currentBreakpoint === 'md';
  const isDesktop = currentBreakpoint === 'lg' || currentBreakpoint === 'xl';

  const responsiveVisibleColumns = useMemo(
    () =>
      getColumnsForBreakpoint(
        visibleColumns,
        responsiveColumns,
        currentBreakpoint,
      ),
    [visibleColumns, responsiveColumns, currentBreakpoint],
  );

  const cardViewData = useMemo((): CardViewRow<T>[] => {
    const colMap = new Map(columns.map((c) => [c.field, c]));
    return data.map((row) => {
      const rk = String((row as Record<string, unknown>)[rowKey]);
      const fields = responsiveVisibleColumns.map((field) => {
        const col = colMap.get(field);
        return {
          field,
          header: col?.header ?? field,
          value: (row as Record<string, unknown>)[field],
        };
      });
      return { key: rk, data: row, fields };
    });
  }, [data, columns, rowKey, responsiveVisibleColumns]);

  return {
    currentBreakpoint,
    isMobile,
    isTablet,
    isDesktop,
    responsiveVisibleColumns,
    cardViewData,
  };
}
