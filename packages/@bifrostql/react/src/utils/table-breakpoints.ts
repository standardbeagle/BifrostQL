import type {
  Breakpoint,
  BreakpointConfig,
  ClientSideFilterConfig,
  ClientSideSortConfig,
  ResponsiveColumnConfig,
  UrlSyncConfig,
} from '../hooks/use-bifrost-table.types';

export const DEFAULT_BREAKPOINTS: BreakpointConfig = {
  xs: 0,
  sm: 640,
  md: 768,
  lg: 1024,
  xl: 1280,
};

export const BREAKPOINT_ORDER: Breakpoint[] = ['xs', 'sm', 'md', 'lg', 'xl'];

export function getBreakpointFromWidth(
  width: number,
  config: BreakpointConfig,
): Breakpoint {
  if (width >= config.xl) return 'xl';
  if (width >= config.lg) return 'lg';
  if (width >= config.md) return 'md';
  if (width >= config.sm) return 'sm';
  return 'xs';
}

export function getColumnsForBreakpoint(
  allColumns: string[],
  responsiveColumns: ResponsiveColumnConfig[] | undefined,
  breakpoint: Breakpoint,
): string[] {
  if (!responsiveColumns || responsiveColumns.length === 0) return allColumns;

  const breakpointIdx = BREAKPOINT_ORDER.indexOf(breakpoint);
  const configMap = new Map(responsiveColumns.map((rc) => [rc.field, rc]));

  return allColumns.filter((field) => {
    const config = configMap.get(field);
    if (!config) return true;
    if (config.minBreakpoint) {
      const minIdx = BREAKPOINT_ORDER.indexOf(config.minBreakpoint);
      return breakpointIdx >= minIdx;
    }
    return true;
  });
}

export function resolveUrlSyncConfig(
  urlSync: boolean | UrlSyncConfig | undefined,
): {
  enabled: boolean;
  prefix: string;
  debounceMs: number;
} {
  if (urlSync === false)
    return { enabled: false, prefix: 'table', debounceMs: 500 };
  if (urlSync === true || urlSync === undefined) {
    return { enabled: true, prefix: 'table', debounceMs: 500 };
  }
  return {
    enabled: urlSync.enabled !== false,
    prefix: urlSync.prefix ?? 'table',
    debounceMs: urlSync.debounceMs ?? 500,
  };
}

export function canAccessWindow(): boolean {
  return typeof window !== 'undefined';
}

export function resolveClientSideSortConfig(
  config: boolean | ClientSideSortConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}

export function resolveClientSideFilterConfig(
  config: boolean | ClientSideFilterConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}
