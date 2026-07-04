import { useEffect, useMemo, useState } from 'react';
import {
  DEFAULT_BREAKPOINTS,
  canAccessWindow,
  getBreakpointFromWidth,
  getColumnsForBreakpoint,
} from '../../utils/table-breakpoints';
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
  const resolvedBreakpoints = useMemo(
    () => ({ ...DEFAULT_BREAKPOINTS, ...breakpointsProp }),
    [breakpointsProp],
  );

  const [windowWidth, setWindowWidth] = useState<number>(() =>
    canAccessWindow() ? window.innerWidth : 1024,
  );

  useEffect(() => {
    if (!canAccessWindow()) return;
    const handleResize = () => setWindowWidth(window.innerWidth);
    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
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
